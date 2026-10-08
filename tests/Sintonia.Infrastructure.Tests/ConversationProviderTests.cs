using System.Collections.Concurrent;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ConversationProviderTests
{
    private static ExecutableLaunch Fixture(string scenario) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [scenario], "Teste");
    private static ConversationRequest Request(string? session = null) => new(AppContext.BaseDirectory, "Pedido de teste ação", NativeSessionId: session);

    [Fact]
    public async Task RpcCorrelatesConcurrentResponsesAndKeepsLargeInputLiteral()
    {
        await using var rpc = new JsonRpcClient(Fixture("rpc-success"), AppContext.BaseDirectory);
        var texts = new[] { new string('á', 60_000) + "\n$(literal)` &|", "segundo" };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var results = await Task.WhenAll(texts.Select(text => rpc.RequestAsync("echo", new { text }, timeout.Token)));
        Assert.Equal(texts, results.Select(r => r.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task CodexUsesTerminalItemsAndResumesSameSession()
    {
        var events = new EventCollector();
        var provider = new CodexConversationProvider(Fixture("rpc-success"));
        var first = await provider.SendAsync(Request(), events, CancellationToken.None);
        var second = await provider.SendAsync(Request(first.NativeSessionId), events, CancellationToken.None);
        Assert.Equal(first.NativeSessionId, second.NativeSessionId);
        Assert.Equal("Resposta final.", first.Text);
        Assert.Equal(ConversationOutcome.Completed, first.Outcome);
        Assert.Contains(events.Events, e => e.Kind == ConversationEventKind.TextDelta);
    }

    [Theory]
    [InlineData("rpc-api")]
    [InlineData("rpc-unsafe")]
    [InlineData("rpc-failed")]
    [InlineData("rpc-exit")]
    public async Task CodexRefusesApiUnsafePolicyFailedTurnOrUnexpectedExit(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ProviderException>(() => new CodexConversationProvider(Fixture(scenario))
            .SendAsync(Request(), new EventCollector(), timeout.Token));
    }

    [Fact]
    public async Task CodexRefusesResumeInAnotherDirectory()
    {
        await Assert.ThrowsAsync<ProviderException>(() => new CodexConversationProvider(Fixture("rpc-wrong-directory"))
            .SendAsync(Request(Guid.NewGuid().ToString()), new EventCollector(), CancellationToken.None));
    }

    [Fact]
    public async Task CodexDeniesApprovalAndReportsBlockedInsteadOfSuccessfulDelivery()
    {
        var events = new EventCollector();
        var result = await new CodexConversationProvider(Fixture("rpc-denied")).SendAsync(Request(), events, CancellationToken.None);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Contains("item/commandExecution/requestApproval", result.PermissionDenials);
        Assert.Contains(events.Events, e => e.Kind == ConversationEventKind.PermissionDenied);
    }

    [Fact]
    public async Task CodexApprovesOnlyThroughExplicitHostDecision()
    {
        ConversationPermission? shown = null;
        var request = Request() with { PermissionHandler = (permission, _) => { shown = permission; return Task.FromResult(true); } };
        var result = await new CodexConversationProvider(Fixture("rpc-denied")).SendAsync(request, new EventCollector(), CancellationToken.None);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome);
        Assert.Equal("comando de teste", shown?.Description);
        Assert.Equal(ProviderKind.Codex, shown?.Provider);
    }

    [Theory]
    [InlineData("rpc-file-preview", true)]
    [InlineData("rpc-file-no-preview", false)]
    public async Task CodexFileApprovalRequiresConcreteDiff(string scenario, bool canReview)
    {
        ConversationPermission? shown = null;
        var request = Request() with { PermissionHandler = (permission, _) => { shown = permission; return Task.FromResult(true); } };
        var result = await new CodexConversationProvider(Fixture(scenario)).SendAsync(request, new EventCollector(), CancellationToken.None);
        Assert.Equal(canReview ? ConversationOutcome.Completed : ConversationOutcome.Blocked, result.Outcome);
        if (canReview) Assert.Contains("-antes\n+depois", shown!.Description);
        else Assert.Null(shown);
    }

    [Fact]
    public async Task CodexCancellationInterruptsActiveTurn()
    {
        using var cancel = new CancellationTokenSource();
        var events = new EventCollector(e => { if (e.Kind == ConversationEventKind.TextDelta) cancel.CancelAfter(50); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexConversationProvider(Fixture("rpc-interrupt"))
            .SendAsync(Request(), events, cancel.Token));
    }

    [Fact]
    public async Task ClaudeTransfersLongPromptByStdinAndResumesExplicitId()
    {
        var provider = new ClaudeConversationProvider(Fixture("claude-success"));
        var prompt = new string('é', 50_000) + "\n$(literal) & ação";
        var first = await provider.SendAsync(Request() with { Prompt = prompt }, new EventCollector(), CancellationToken.None);
        var second = await provider.SendAsync(Request(first.NativeSessionId), new EventCollector(), CancellationToken.None);
        Assert.Equal(prompt, first.Text);
        Assert.Equal(first.NativeSessionId, second.NativeSessionId);
    }

    [Theory]
    [InlineData("claude-api")]
    [InlineData("claude-failed")]
    [InlineData("claude-exit")]
    public async Task ClaudeRefusesApiFailedResultOrUnexpectedExit(string scenario)
    {
        await Assert.ThrowsAsync<ProviderException>(() => new ClaudeConversationProvider(Fixture(scenario))
            .SendAsync(Request(), new EventCollector(), CancellationToken.None));
    }

    [Fact]
    public async Task ClaudeReportsDeniedToolAsBlocked()
    {
        var result = await new ClaudeConversationProvider(Fixture("claude-denied"))
            .SendAsync(Request(), new EventCollector(), CancellationToken.None);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("Write", Assert.Single(result.PermissionDenials));
    }

    [Fact]
    public async Task ClaudeCancellationStopsProcess()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeConversationProvider(Fixture("claude-wait"))
            .SendAsync(Request(), new EventCollector(), timeout.Token));
    }

    [Theory]
    [InlineData("claude-host-write", "Write", "ação.txt")]
    [InlineData("claude-host-edit", "Edit", "antes")]
    [InlineData("claude-host-command", "Bash", "$(não executar)")]
    [InlineData("claude-host-mcp", "mcp__example__action", "ação externa de teste")]
    public async Task ClaudeApprovalShowsCompleteInputAndPreservesOnlyThisAction(string scenario, string tool, string preview)
    {
        ConversationPermission? shown = null;
        var request = Request() with
        {
            Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (permission, _) => { shown = permission; return Task.FromResult(true); }
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture(scenario)).SendAsync(request, new EventCollector(), timeout.Token);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome);
        Assert.Equal(ProviderKind.Claude, shown?.Provider);
        Assert.Equal(tool, shown?.Kind);
        Assert.Equal(request.WorkingDirectory, shown?.WorkingDirectory);
        Assert.Contains(preview, shown!.Description);
        if (tool == "Edit") Assert.Contains("depois", shown.Description);
    }

    [Theory]
    [InlineData("claude-host-no-preview")]
    [InlineData("claude-host-large")]
    [InlineData("claude-host-question")]
    [InlineData("claude-host-unknown")]
    public async Task ClaudeRefusesIncompleteOversizedOrUnsupportedRequestsWithoutApproval(string scenario)
    {
        var called = false;
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (_, _) => { called = true; return Task.FromResult(true); } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture(scenario)).SendAsync(request, new EventCollector(), timeout.Token);
        Assert.False(called);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
    }

    [Theory]
    [InlineData(ConversationAccess.ReadOnly, true, true)]
    [InlineData(ConversationAccess.WorkspaceWrite, false, true)]
    [InlineData(ConversationAccess.WorkspaceWrite, true, false)]
    public async Task ClaudeDenialDoesNotDependOnProviderRepeatingItInFinalResult(ConversationAccess access, bool withHost, bool approve)
    {
        var calls = 0;
        var request = Request() with { Access = access, PermissionHandler = withHost
            ? (_, _) => { calls++; return Task.FromResult(approve); } : null };
        var events = new EventCollector();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture("claude-host-write")).SendAsync(request, events, timeout.Token);
        Assert.Equal(access == ConversationAccess.WorkspaceWrite && withHost ? 1 : 0, calls);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("Write", Assert.Single(result.PermissionDenials));
        Assert.Single(events.Events, e => e.Kind == ConversationEventKind.PermissionDenied);
    }

    [Fact]
    public async Task ClaudeHostFailureRefusesAction()
    {
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (_, _) => throw new InvalidOperationException("Falha da interface") };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture("claude-host-write")).SendAsync(request, new EventCollector(), timeout.Token);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
    }

    [Fact]
    public async Task ClaudeKeepsReadingEventsWhileHostWaitsForDecision()
    {
        var streamed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new EventCollector(e => { if (e.Text == "Evento durante autorização") streamed.TrySetResult(true); });
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = async (_, token) => { await streamed.Task.WaitAsync(token); return true; } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture("claude-host-write")).SendAsync(request, events, timeout.Token);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome);
    }

    [Theory]
    [InlineData("claude-host-cancel", false)]
    [InlineData("claude-host-exit", true)]
    public async Task ClaudeWithdrawsPendingHostDecisionOnCancellationOrExit(string scenario, bool exits)
    {
        var asked = false;
        var cancelled = false;
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite, PermissionHandler = async (_, token) =>
        {
            asked = true;
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { cancelled = true; throw; }
            return true;
        } };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = new ClaudeConversationProvider(Fixture(scenario)).SendAsync(request, new EventCollector(), timeout.Token);
        if (exits) await Assert.ThrowsAsync<ProviderException>(() => task);
        else Assert.Equal(ConversationOutcome.Completed, (await task).Outcome);
        Assert.True(asked);
        Assert.True(cancelled);
    }

    [Fact]
    public async Task ClaudeUserCancellationStopsPendingDecisionEvenIfHostIgnoresToken()
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (_, _) => { stop.CancelAfter(50); return answer.Task; } };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ClaudeConversationProvider(Fixture("claude-host-write"))
            .SendAsync(request, new EventCollector(), stop.Token));
        answer.TrySetResult(true);
    }

    [Theory]
    [InlineData("claude-init-failed")]
    [InlineData("claude-host-duplicate")]
    [InlineData("claude-host-premature-result")]
    public async Task ClaudeRejectsFailedInitializationAndInvalidPermissionLifecycle(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = async (_, token) => { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; } };
        await Assert.ThrowsAsync<ProviderException>(() => new ClaudeConversationProvider(Fixture(scenario))
            .SendAsync(request, new EventCollector(), timeout.Token));
    }

    [Fact]
    public async Task ClaudeApprovalDoesNotAuthorizeNextAction()
    {
        var calls = 0;
        var request = Request() with { Access = ConversationAccess.WorkspaceWrite,
            PermissionHandler = (_, _) => Task.FromResult(++calls == 1) };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await new ClaudeConversationProvider(Fixture("claude-host-two"))
            .SendAsync(request, new EventCollector(), timeout.Token);
        Assert.Equal(2, calls);
        Assert.Equal(ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal("Write", Assert.Single(result.PermissionDenials));
    }

    [Fact]
    public void ParserIgnoresNewFieldsButRequiresSessionAndTerminalSuccess()
    {
        var id = Guid.NewGuid().ToString();
        var parser = new ClaudeStreamParser(id, new EventCollector());
        parser.Accept(JsonSerializer.Serialize(new { type = "new_event", future_field = true }));
        Assert.Throws<ProviderException>(() => parser.GetResult(0));
        Assert.Throws<ProviderException>(() => parser.Accept(JsonSerializer.Serialize(new { type = "system", session_id = Guid.NewGuid().ToString() })));
    }

    private sealed class EventCollector(Action<ConversationEvent>? callback = null) : IProgress<ConversationEvent>
    {
        public ConcurrentQueue<ConversationEvent> Events { get; } = new();
        public void Report(ConversationEvent value) { Events.Enqueue(value); callback?.Invoke(value); }
    }
}
