using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeQuestionProbeTests
{
    [Fact]
    public async Task FiniteProofAnswersOneExactQuestionAndVerifierNeverCallsModels()
    {
        using var folder = new Folder(); var provider = new Simulation();
        await ClaudeQuestionProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None);
        ClaudeQuestionProbe.Verify(folder.Path); Assert.Equal(1, provider.Calls); Assert.Equal(1, provider.Answers);
        var path = Path.Combine(folder.Path, "question-evidence.json"); var original = File.ReadAllText(path);
        Assert.DoesNotContain(ClaudeQuestionProbe.Question, original); // No tool result body or model reply.
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeQuestionProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None));
        Assert.Equal(1, provider.Calls); Assert.Equal(original, File.ReadAllText(path));
        File.WriteAllText(path, original.Replace("Completed", "Interrupted", StringComparison.Ordinal));
        Assert.Throws<ProviderException>(() => ClaudeQuestionProbe.Verify(folder.Path));
    }

    [Theory]
    [InlineData("missing-question")]
    [InlineData("wrong-question")]
    [InlineData("duplicate-question")]
    [InlineData("permission")]
    [InlineData("extra-tool")]
    [InlineData("duplicate-tool")]
    [InlineData("missing-result")]
    [InlineData("extra-result")]
    [InlineData("error-result")]
    [InlineData("wrong-answer-result")]
    [InlineData("truncated")]
    [InlineData("wrong-final")]
    [InlineData("blocked")]
    [InlineData("cancelled")]
    [InlineData("file-effect")]
    [InlineData("oauth-refresh")]
    public async Task DivergenceStopsAfterOneAttemptAndPreservesPartialEvidence(string mode)
    {
        using var folder = new Folder(); var provider = new Simulation(mode);
        await Assert.ThrowsAnyAsync<Exception>(() => ClaudeQuestionProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, "question-evidence.json")));
        Assert.Equal("Interrupted", doc.RootElement.GetProperty("State").GetString());
        if (mode == "file-effect") Assert.True(File.Exists(Path.Combine(folder.Path, "SIMULAÇÃO.txt")));
        if (mode == "wrong-question") Assert.Equal(0, provider.Answers);
        if (mode == "duplicate-question") Assert.Equal(1, provider.Answers);
        if (mode == "oauth-refresh")
        {
            Assert.Equal("OAuthRefreshFailed", doc.RootElement.GetProperty("FailureCode").GetString());
            Assert.DoesNotContain("PRIVATE", doc.RootElement.GetRawText());
        }
    }

    [Theory]
    [InlineData("directory")]
    [InlineData("provider")]
    [InlineData("question")]
    [InlineData("option")]
    [InlineData("multi")]
    [InlineData("preview")]
    public void AutomaticResponseRequiresTheExactDisplayedQuestionAndChoices(string change)
    {
        var request = new ConversationQuestionRequest(ProviderKind.Claude, AppContext.BaseDirectory, [ClaudeQuestionProbe.Expected]);
        var question = request.Questions[0];
        request = change switch
        {
            "directory" => request with { WorkingDirectory = Path.Combine(AppContext.BaseDirectory, "elsewhere") },
            "provider" => request with { Provider = ProviderKind.Codex },
            "question" => request with { Questions = [question with { Text = "Different question?" }] },
            "multi" => request with { Questions = [question with { MultiSelect = true }] },
            "preview" => request with { Questions = [question with { Choices = [question.Choices[0] with { Preview = "Different preview" }, question.Choices[1]] }] },
            _ => request with { Questions = [question with { Choices = [question.Choices[0] with { Label = "Different option" }, question.Choices[1]] }] }
        };
        Assert.False(ClaudeQuestionProbe.Matches(request, AppContext.BaseDirectory));
    }

    [Fact]
    public void ProofFlagsLimitToolsAndIterationsWithoutBypassOrGlobalSettings()
    {
        var prefix = new[] { "native-prefix" };
        var launch = ClaudeQuestionProbe.CreateLaunch(new("native.exe", prefix, "simulation"));
        Assert.Equal("native-prefix", launch.PrefixArguments[0]); Assert.Equal(new[] { "--max-turns", "3" }, launch.PrefixArguments.TakeLast(2));
        Assert.Contains("--strict-mcp-config", launch.PrefixArguments);
        Assert.Contains("{\"mcpServers\":{}}", launch.PrefixArguments);
        Assert.Equal(ClaudeQuestionProbe.Tool, launch.PrefixArguments[launch.PrefixArguments.ToList().IndexOf("--tools") + 1]);
        Assert.DoesNotContain(launch.PrefixArguments, argument => argument.Contains("skip-permissions", StringComparison.Ordinal));
        Assert.DoesNotContain("--allowedTools", launch.PrefixArguments);
    }

    [Fact]
    public async Task ReadinessInitializesTheSameReadOnlyControlWithoutSendingAPrompt()
    {
        using var folder = new Folder(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var launch = new ExecutableLaunch(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), ["claude-question"], "Teste");
        await ClaudeQuestionProbe.CheckReadyAsync(launch, folder.Path, stop.Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder.Path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NativeOAuthErrorKeepsItsActionableKindEvenWhenTheCliExitsWithAnError(int exitCode)
    {
        var parser = new ClaudeStreamParser("simulation-session", new Progress<ConversationEvent>());
        parser.Accept(JsonSerializer.Serialize(new { type = "result", session_id = "simulation-session", is_error = true,
            subtype = "error_during_execution", errors = new[] { "OAuth token refresh failed: PRIVATE native details." } }));
        var error = Assert.Throws<ProviderException>(() => parser.GetResult(exitCode));
        Assert.Equal(ProviderFailureKind.AuthenticationRefreshFailed, error.FailureKind);
        Assert.Contains("Refaça o login por assinatura", error.Message); Assert.DoesNotContain("PRIVATE", error.Message);
    }

    [Fact]
    public void OtherNativeErrorsDoNotInventAnAuthenticationFailure()
    {
        var parser = new ClaudeStreamParser("simulation-session", new Progress<ConversationEvent>());
        parser.Accept(JsonSerializer.Serialize(new { type = "result", session_id = "simulation-session", is_error = true,
            subtype = "error_during_execution", errors = new[] { "SIMULAÇÃO: erro sem OAuth." } }));
        var error = Assert.Throws<ProviderException>(() => parser.GetResult(1));
        Assert.Equal(ProviderFailureKind.Unspecified, error.FailureKind);
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sintonia-question-proof-" + Guid.NewGuid());
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>In-memory callback/result simulation; no process, network or model.</summary>
    private sealed class Simulation(string mode = "") : IConversationProvider
    {
        private Action<ClaudeToolResult> _observe = _ => { };
        public ProviderKind Kind => ProviderKind.Claude;
        public int Calls { get; private set; }
        public int Answers { get; private set; }
        public IConversationProvider Create(Action<ClaudeToolResult> observe) { _observe = observe; return this; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++; Assert.Equal(ClaudeQuestionProbe.Prompt, request.Prompt);
            Assert.Equal(ConversationAccess.ReadOnly, request.Access); Assert.Null(request.NativeSessionId);
            if (mode == "oauth-refresh") throw new ProviderException("Falha de teste PRIVATE.", ProviderFailureKind.AuthenticationRefreshFailed);
            var question = new ConversationQuestionRequest(ProviderKind.Claude, request.WorkingDirectory, [ClaudeQuestionProbe.Expected]);
            if (mode == "wrong-question") question = question with { Questions = [question.Questions[0] with { Text = "Different?" }] };
            if (mode != "missing-question")
            {
                var response = await request.QuestionHandler!(question, token);
                if (response is not null)
                {
                    Answers++; Assert.True(ConversationQuestionPolicy.TryGetAnswers(question, response, out var values));
                    Assert.Equal(ClaudeQuestionProbe.Answer, values[question.Questions[0].Text]);
                }
                if (mode == "duplicate-question") Assert.Null(await request.QuestionHandler!(question, token));
            }
            if (mode == "permission") Assert.False(await request.PermissionHandler!(new(Kind, "Write", "SIMULAÇÃO", request.WorkingDirectory), token));
            progress.Report(new(ConversationEventKind.Tool, ClaudeQuestionProbe.Tool));
            if (mode is "extra-tool" or "duplicate-tool") progress.Report(new(ConversationEventKind.Tool, mode == "extra-tool" ? "Other" : ClaudeQuestionProbe.Tool));
            if (mode is "cancelled") throw new OperationCanceledException();
            if (mode != "missing-result")
                _observe(new("simulation-call", ClaudeQuestionProbe.Tool, mode == "error-result",
                    ClaudeQuestionProbe.Question + ": " + (mode == "wrong-answer-result" ? "Resumo" : ClaudeQuestionProbe.Answer), mode == "truncated"));
            if (mode == "extra-result") _observe(new("extra-call", ClaudeQuestionProbe.Tool, false, ClaudeQuestionProbe.Question + ClaudeQuestionProbe.Answer, false));
            if (mode == "file-effect") await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "SIMULAÇÃO.txt"), "SIMULAÇÃO");
            return new("simulation-session", "simulation-model", mode == "wrong-final" ? "{\"formato\":\"Resumo\"}" : "{\"formato\":\"Detalhes\"}",
                mode == "blocked" ? ConversationOutcome.Blocked : ConversationOutcome.Completed, []);
        }
    }
}
