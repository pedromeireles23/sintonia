using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeQuestionTests
{
    public static string Input(bool multiple = false) => JsonSerializer.Serialize(new { questions = new[]
        { new { question = "Qual formato?", header = "Formato", multiSelect = multiple, options = new[]
            { new { label = "Resumo", description = "Texto curto.", preview = "<b>Somente texto</b>" }, new { label = "Detalhes", description = "Texto completo.", preview = "" } } } },
        answers = new { PreFilled = "Não reutilizar" }, metadata = new { literal = "$(não executar)" } });
    private static ClaudeConversationProvider Provider(string mode = "claude-question") => new(new ExecutableLaunch(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [mode], "Teste"));
    private static ConversationRequest Request(string input) => new(AppContext.BaseDirectory, input);

    [Theory]
    [InlineData(ConversationAccess.ReadOnly, false)]
    [InlineData(ConversationAccess.WorkspaceWrite, true)]
    public async Task AnswersThroughSeparateCallbackAndRetainsOriginalInput(ConversationAccess access, bool multiple)
    {
        var calls = 0; var approvals = 0; var events = new Events();
        var request = Request(Input(multiple)) with { Access = access,
            PermissionHandler = (_, _) => { approvals++; return Task.FromResult(true); },
            QuestionHandler = (question, _) =>
            {
                calls++; Assert.Equal(ProviderKind.Claude, question.Provider); Assert.Equal(AppContext.BaseDirectory, question.WorkingDirectory);
                Assert.Equal("<b>Somente texto</b>", question.Questions[0].Choices[0].Preview);
                return Task.FromResult<ConversationQuestionResponse?>(new([new(multiple ? ["Resumo", "Detalhes"] : ["Resumo"])]));
            } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await Provider().SendAsync(request, events, timeout.Token);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome); Assert.Equal(1, calls); Assert.Equal(0, approvals);
        using var answers = JsonDocument.Parse(result.Text);
        if (multiple) Assert.Equal(new[] { "Resumo", "Detalhes" }, answers.RootElement.GetProperty("Qual formato?").EnumerateArray().Select(item => item.GetString()));
        else Assert.Equal("Resumo", answers.RootElement.GetProperty("Qual formato?").GetString());
        Assert.False(answers.RootElement.TryGetProperty("PreFilled", out _));
        Assert.Contains(events.Items, item => item.Kind == ConversationEventKind.TextDelta && item.Text == "DURANTE-PERGUNTA");
    }

    [Theory]
    [InlineData("no-handler")]
    [InlineData("declined")]
    [InlineData("invalid-answer")]
    [InlineData("handler-failure")]
    public async Task MissingOrRejectedAnswersNeverFallThroughToPermissionApproval(string mode)
    {
        var approvals = 0;
        var request = Request(Input()) with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (_, _) => { approvals++; return Task.FromResult(true); },
            QuestionHandler = mode == "no-handler" ? null : (_, _) => mode == "handler-failure" ? throw new InvalidOperationException("PRIVATE")
                : Task.FromResult<ConversationQuestionResponse?>(mode == "invalid-answer" ? new([new(["not shown"])]) : null) };
        var result = await Provider().SendAsync(request, new Events(), CancellationToken.None);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome); Assert.Contains("AskUserQuestion", result.PermissionDenials); Assert.Equal(0, approvals);
    }

    [Theory]
    [InlineData("{\"questions\":[]}")]
    [InlineData("{\"questions\":123}")]
    [InlineData("{\"questions\":[{\"question\":\"q\",\"header\":\"h\",\"options\":[]}]}")]
    [InlineData("{\"questions\":[],\"questions\":[]}")]
    [InlineData("{\"questions\":[{\"question\":\"q\",\"header\":\"h\",\"options\":[{\"label\":\"a\",\"label\":\"b\",\"description\":\"d\"},{\"label\":\"c\",\"description\":\"d\"}]}]}")]
    public async Task MalformedQuestionNeverDisplaysOrApproves(string input)
    {
        var called = false;
        var result = await Provider().SendAsync(Request(input) with { QuestionHandler = (_, _) =>
            { called = true; return Task.FromResult<ConversationQuestionResponse?>(null); } }, new Events(), CancellationToken.None);
        Assert.False(called); Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task WithdrawalCancelsPendingQuestionWithoutLateAnswer()
    {
        var cancelled = false;
        var result = await Provider("claude-question-withdraw").SendAsync(Request(Input()) with { QuestionHandler = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return null; }
            catch (OperationCanceledException) { cancelled = true; throw; }
        } }, new Events(), CancellationToken.None);
        Assert.True(cancelled); Assert.Equal(ConversationOutcome.Completed, result.Outcome);
    }

    [Theory]
    [InlineData("claude-question-exit")]
    [InlineData("claude-question-duplicate")]
    [InlineData("claude-question-premature")]
    public async Task ProtocolFailureCancelsTheDisplayedQuestion(string scenario)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = Request(Input()) with { QuestionHandler = async (_, token) =>
        {
            try { await Task.Delay(Timeout.Infinite, token); return null; }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ProviderException>(() => Provider(scenario).SendAsync(request, new Events(), timeout.Token));
        await cancelled.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task ExternalCancellationInterruptsWaitingQuestion()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = Request(Input()) with { QuestionHandler = async (_, token) =>
            { stop.Cancel(); await Task.Delay(Timeout.Infinite, token); return null; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider().SendAsync(request, new Events(), stop.Token));
    }

    [Fact]
    public async Task ChiefAndReadOnlyChatKeepQuestionHandlerWithoutAuthorizingWrite()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Sintonia-question-store-" + Guid.NewGuid()); Directory.CreateDirectory(directory);
        try
        {
            var store = new SqliteWorkspaceStore(Path.Combine(directory, "history.db")); await store.InitializeAsync();
            var project = await store.AddProjectAsync(directory);
            var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Chefia de teste", ProviderKind.Claude,
                null, null, PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.WorkspaceWrite);
            await store.SaveConversationAsync(conversation);
            var calls = 0; var approvals = 0;
            var run = await new WorkspaceChatService(store, [Provider("claude-question-write")]).SendAsync(project, conversation, Input(), new Events(), CancellationToken.None,
                (_, _) => { approvals++; return Task.FromResult(true); }, (_, _) => { calls++; return Task.FromResult<ConversationQuestionResponse?>(new([new([], "ação literal $(não executar)")])); });
            Assert.Equal(ChatRunState.Blocked, run.State); Assert.Equal(1, calls); Assert.Equal(0, approvals);
            using var answer = JsonDocument.Parse(run.Response!);
            Assert.Equal("ação literal $(não executar)", answer.RootElement.GetProperty("Qual formato?").GetString());
            Assert.Contains("Write", run.Error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Events : IProgress<ConversationEvent>
    {
        public List<ConversationEvent> Items { get; } = [];
        public void Report(ConversationEvent value) { lock (Items) Items.Add(value); }
    }
}
