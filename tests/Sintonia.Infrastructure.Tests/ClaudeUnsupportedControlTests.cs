using System.Collections.Concurrent;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeUnsupportedControlTests
{
    private const string PrivateText = "NATIVE_PRIVATE_DO_NOT_DISPLAY";
    private static ClaudeConversationProvider Provider(string scenario = "claude-control") => new(new ExecutableLaunch(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [scenario], "Teste"));
    private static string Elicitation(string mode) => mode switch
    {
        "omitted" => JsonSerializer.Serialize(new { subtype = "elicitation", mcp_server_name = PrivateText, message = PrivateText }),
        "malformed" => "{\"subtype\":\"elicitation\",\"message\":123,\"requested_schema\":[]}",
        _ => JsonSerializer.Serialize(new { subtype = "elicitation", mcp_server_name = PrivateText, message = PrivateText,
            mode, url = "https://example.invalid/" + PrivateText, elicitation_id = PrivateText,
            requested_schema = new { type = "object", properties = new { token = new { type = "string" } } } })
    };
    private static ConversationRequest Request(string control, string expected, ConversationAccess access,
        string id = "special-1", string expectedWrite = "deny") => new(AppContext.BaseDirectory,
        JsonSerializer.Serialize(new { requestId = id, request = JsonSerializer.Deserialize<JsonElement>(control), expected, expectedWrite }), Access: access);

    public static TheoryData<string, ConversationAccess> Elicitations => new()
    {
        { "form", ConversationAccess.ReadOnly }, { "form", ConversationAccess.WorkspaceWrite },
        { "url", ConversationAccess.ReadOnly }, { "url", ConversationAccess.WorkspaceWrite },
        { "omitted", ConversationAccess.ReadOnly }, { "omitted", ConversationAccess.WorkspaceWrite },
        { "future-mode", ConversationAccess.ReadOnly }, { "future-mode", ConversationAccess.WorkspaceWrite },
        { "malformed", ConversationAccess.ReadOnly }, { "malformed", ConversationAccess.WorkspaceWrite }
    };

    [Theory]
    [MemberData(nameof(Elicitations))]
    public async Task ElicitationDeclinesWithoutUserDataCallbacksOrSuccessfulDelivery(string mode, ConversationAccess access)
    {
        var permissions = 0; var questions = 0; var events = new Events();
        var request = WithCallbacks(Request(Elicitation(mode), "decline", access), () => permissions++, () => questions++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await Provider().SendAsync(request, events, timeout.Token);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("elicitation MCP", Assert.Single(result.PermissionDenials));
        Assert.Equal(0, permissions); Assert.Equal(0, questions);
        Assert.Contains(events.Items, item => item.Kind == ConversationEventKind.Diagnostic && item.Text.Contains("elicitation"));
        Assert.Contains(events.Items, item => item.Text == "DURANTE-CONTROLE");
        Assert.DoesNotContain(events.Items, item => item.Text.Contains(PrivateText));
        Assert.DoesNotContain(PrivateText, result.Text);
    }

    [Theory]
    [InlineData(ConversationAccess.ReadOnly, "{}")]
    [InlineData(ConversationAccess.WorkspaceWrite, "{}")]
    [InlineData(ConversationAccess.ReadOnly, "{\"allowedPrompts\":[{\"tool\":\"Bash\",\"prompt\":\"NATIVE_PRIVATE_DO_NOT_DISPLAY\"}]}")]
    [InlineData(ConversationAccess.WorkspaceWrite, "{\"allowedPrompts\":[{\"tool\":\"Bash\",\"prompt\":\"NATIVE_PRIVATE_DO_NOT_DISPLAY\"}]}")]
    public async Task ExitPlanModeNeverUsesBooleanApprovalOrQuestionAnswers(ConversationAccess access, string input)
    {
        var callbacks = 0; var events = new Events();
        var request = WithCallbacks(Request(PlanExit(input), "deny", access), () => callbacks++, () => callbacks++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await Provider().SendAsync(request, events, timeout.Token);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("ExitPlanMode", Assert.Single(result.PermissionDenials));
        Assert.Equal(0, callbacks);
        Assert.Contains(events.Items, item => item.Kind == ConversationEventKind.Diagnostic && item.Text.Contains("Saída do planejamento"));
        Assert.DoesNotContain(events.Items, item => item.Text.Contains(PrivateText));
    }

    [Theory]
    [InlineData(ConversationAccess.ReadOnly)]
    [InlineData(ConversationAccess.WorkspaceWrite)]
    public async Task UnknownControlFailsWithoutOfferingAnApproval(ConversationAccess access)
    {
        var callbacks = 0;
        var request = WithCallbacks(Request("{\"subtype\":\"future_control\",\"message\":\"NATIVE_PRIVATE_DO_NOT_DISPLAY\"}", "error", access),
            () => callbacks++, () => callbacks++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await Provider().SendAsync(request, new Events(), timeout.Token);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome); Assert.Equal(0, callbacks);
        Assert.Equal("controle não suportado", Assert.Single(result.PermissionDenials));
    }

    [Theory]
    [InlineData("{}", "special-1")]
    [InlineData("{\"subtype\":[]}", "special-1")]
    [InlineData("{\"subtype\":\"elicitation\"}", "")]
    [InlineData("{\"subtype\":\"elicitation\"}", "oversized")]
    public async Task InvalidEnvelopeStopsTheProtocolWithoutAnyHostCallback(string control, string id)
    {
        var callbacks = 0;
        var request = WithCallbacks(Request(control, "decline", ConversationAccess.WorkspaceWrite,
            id == "oversized" ? new string('x', 257) : id), () => callbacks++, () => callbacks++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<ProviderException>(() => Provider("claude-control-invalid").SendAsync(request, new Events(), timeout.Token));
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData("claude-control-duplicate", true)]
    [InlineData("claude-control-duplicate", false)]
    [InlineData("claude-control-late", true)]
    [InlineData("claude-control-late", false)]
    public async Task DuplicatedOrPostTerminalControlCannotBecomeASuccessfulRun(string scenario, bool elicitation)
    {
        var callbacks = 0;
        var request = WithCallbacks(Request(elicitation ? Elicitation("form") : PlanExit("{}"), elicitation ? "decline" : "deny",
            ConversationAccess.WorkspaceWrite), () => callbacks++, () => callbacks++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await Assert.ThrowsAsync<ProviderException>(() => Provider(scenario).SendAsync(request, new Events(), timeout.Token));
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(true, ConversationAccess.ReadOnly)]
    [InlineData(true, ConversationAccess.WorkspaceWrite)]
    [InlineData(false, ConversationAccess.ReadOnly)]
    [InlineData(false, ConversationAccess.WorkspaceWrite)]
    public async Task RefusalKeepsAccessAndRequiresSeparateDecisionForFollowingWrite(bool elicitation, ConversationAccess access)
    {
        var permissions = 0; var questions = 0;
        var writable = access == ConversationAccess.WorkspaceWrite;
        var request = WithCallbacks(Request(elicitation ? Elicitation("url") : PlanExit("{}"), elicitation ? "decline" : "deny", access,
            expectedWrite: writable ? "allow" : "deny"), () => permissions++, () => questions++);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await Provider("claude-control-followup").SendAsync(request, new Events(), timeout.Token);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal(writable ? 1 : 0, permissions); Assert.Equal(0, questions);
        Assert.Contains(elicitation ? "elicitation MCP" : "ExitPlanMode", result.PermissionDenials);
        Assert.Equal(writable ? 1 : 2, result.PermissionDenials.Count);
    }

    [Fact]
    public async Task ElicitationDoesNotBlockStreamingOrInheritPendingWriteApproval()
    {
        var streamed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var events = new Events(item => { if (item.Text == "DURANTE-CONTROLE") streamed.TrySetResult(); });
        var request = Request(Elicitation("form"), "decline", ConversationAccess.WorkspaceWrite, expectedWrite: "allow") with
        {
            PermissionHandler = async (_, token) => { calls++; await streamed.Task.WaitAsync(token); return true; },
            QuestionHandler = (_, _) => throw new InvalidOperationException("Elicitation não é AskUserQuestion.")
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var result = await Provider("claude-control-interleaved").SendAsync(request, events, timeout.Token);
        Assert.Equal(1, calls); Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("elicitation MCP", Assert.Single(result.PermissionDenials));
    }

    private static string PlanExit(string input) => JsonSerializer.Serialize(new
        { subtype = "can_use_tool", tool_name = "ExitPlanMode", input = JsonSerializer.Deserialize<JsonElement>(input) });
    private static ConversationRequest WithCallbacks(ConversationRequest request, Action permissions, Action questions) => request with
    {
        PermissionHandler = (_, _) => { permissions(); return Task.FromResult(true); },
        QuestionHandler = (_, _) => { questions(); return Task.FromResult<ConversationQuestionResponse?>(new([new([], "NÃO-ENVIAR")])); }
    };
    private sealed class Events(Action<ConversationEvent>? callback = null) : IProgress<ConversationEvent>
    {
        public ConcurrentQueue<ConversationEvent> Items { get; } = new();
        public void Report(ConversationEvent value) { Items.Enqueue(value); callback?.Invoke(value); }
    }
}
