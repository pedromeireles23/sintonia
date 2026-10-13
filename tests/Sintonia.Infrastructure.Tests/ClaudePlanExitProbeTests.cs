using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudePlanExitProbeTests
{
    [Fact]
    public async Task RealAdapterWithTestProcessConfirmsHostRefusalAndBlockedResult()
    {
        using var folder = new Folder();
        var launch = new ExecutableLaunch(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), ["claude-host-plan-exit"], "Teste");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ClaudePlanExitProbe.RunCaseAsync(folder.Path, observe => new ClaudeConversationProvider(launch, observe), "simulation-version", timeout.Token);
        ClaudePlanExitProbe.Verify(folder.Path);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, ClaudePlanExitProbe.EvidenceFile)));
        Assert.Equal("Blocked", evidence.RootElement.GetProperty("Outcome").GetString());
        Assert.Equal(0, evidence.RootElement.GetProperty("Permissions").GetInt32());
        Assert.DoesNotContain(ClaudePlanExitProbe.Prompt, evidence.RootElement.GetRawText());
    }

    [Fact]
    public async Task CompletedProofCannotBeReplayedAndVerifierRejectsChangedState()
    {
        using var folder = new Folder(); var provider = new Simulation();
        await ClaudePlanExitProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None);
        ClaudePlanExitProbe.Verify(folder.Path);
        var path = Path.Combine(folder.Path, ClaudePlanExitProbe.EvidenceFile); var original = File.ReadAllText(path);
        await Assert.ThrowsAsync<ProviderException>(() => ClaudePlanExitProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None));
        Assert.Equal(1, provider.Calls); Assert.Equal(original, File.ReadAllText(path));
        File.WriteAllText(path, original.Replace("Completed", "Interrupted", StringComparison.Ordinal));
        Assert.Throws<ProviderException>(() => ClaudePlanExitProbe.Verify(folder.Path));
    }

    [Theory]
    [InlineData("missing-tool")]
    [InlineData("extra-tool")]
    [InlineData("wrong-tool")]
    [InlineData("missing-host")]
    [InlineData("duplicate-host")]
    [InlineData("missing-denial")]
    [InlineData("wrong-denial")]
    [InlineData("permission")]
    [InlineData("question")]
    [InlineData("missing-result")]
    [InlineData("allowed-result")]
    [InlineData("wrong-result")]
    [InlineData("truncated")]
    [InlineData("extra-result")]
    [InlineData("completed")]
    [InlineData("wrong-denials")]
    [InlineData("cancelled")]
    [InlineData("file-effect")]
    [InlineData("oauth-refresh")]
    public async Task DivergenceStopsAfterOneSendAndPreservesSafePartialEvidence(string mode)
    {
        using var folder = new Folder(); var provider = new Simulation(mode);
        await Assert.ThrowsAnyAsync<Exception>(() => ClaudePlanExitProbe.RunCaseAsync(folder.Path, provider.Create, "simulation-version", CancellationToken.None));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, ClaudePlanExitProbe.EvidenceFile)));
        Assert.Equal("Interrupted", doc.RootElement.GetProperty("State").GetString()); Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain("NATIVE_PRIVATE", doc.RootElement.GetRawText());
        if (mode == "oauth-refresh") Assert.Equal("OAuthRefreshFailed", doc.RootElement.GetProperty("FailureCode").GetString());
    }

    [Fact]
    public void LaunchScopesOneToolEmptyMcpAndTwoInternalIterationsWithoutBypass()
    {
        var launch = ClaudePlanExitProbe.CreateLaunch(new("claude.exe", [], "Claude"));
        Assert.Contains("--strict-mcp-config", launch.PrefixArguments); Assert.Contains(ClaudePlanExitProbe.Tool, launch.PrefixArguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", launch.PrefixArguments);
        Assert.Equal("2", launch.PrefixArguments[^1]);
        using var settings = JsonDocument.Parse(launch.PrefixArguments[1]);
        Assert.Equal(ClaudePlanExitProbe.Tool, Assert.Single(settings.RootElement.GetProperty("permissions").GetProperty("ask").EnumerateArray()).GetString());
    }

    private sealed class Simulation(string mode = "") : IConversationProvider
    {
        private Action<ClaudeToolResult> _observe = _ => { };
        public ProviderKind Kind => ProviderKind.Claude;
        public int Calls { get; private set; }
        public IConversationProvider Create(Action<ClaudeToolResult> observe) { _observe = observe; return this; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++; Assert.Equal(ConversationAccess.ReadOnly, request.Access); Assert.Equal(ClaudePlanExitProbe.Prompt, request.Prompt);
            Assert.Null(request.NativeSessionId);
            if (mode == "oauth-refresh") throw new ProviderException("NATIVE_PRIVATE", ProviderFailureKind.AuthenticationRefreshFailed);
            if (mode != "missing-tool") progress.Report(new(ConversationEventKind.Tool, mode == "wrong-tool" ? "Write" : ClaudePlanExitProbe.Tool));
            if (mode == "extra-tool") progress.Report(new(ConversationEventKind.Tool, ClaudePlanExitProbe.Tool));
            if (mode != "missing-host") progress.Report(new(ConversationEventKind.Diagnostic, ClaudePlanExitProbe.HostDiagnostic + "SIMULAÇÃO"));
            if (mode == "duplicate-host") progress.Report(new(ConversationEventKind.Diagnostic, ClaudePlanExitProbe.HostDiagnostic + "SIMULAÇÃO"));
            if (mode != "missing-denial") progress.Report(new(ConversationEventKind.PermissionDenied, mode == "wrong-denial" ? "Write" : ClaudePlanExitProbe.Denial));
            if (mode == "permission") Assert.False(await request.PermissionHandler!(new(Kind, "Write", "SIMULAÇÃO", request.WorkingDirectory), token));
            if (mode == "question") Assert.Null(await request.QuestionHandler!(new(Kind, request.WorkingDirectory, [ClaudeQuestionProbe.Expected]), token));
            if (mode != "missing-result") _observe(Result("one"));
            if (mode == "extra-result") _observe(Result("two"));
            if (mode == "cancelled") throw new OperationCanceledException();
            if (mode == "file-effect") await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "SIMULAÇÃO.txt"), "SIMULAÇÃO");
            return new("simulation-session", "simulation-model", "SIMULAÇÃO: ação recusada.",
                mode == "completed" ? ConversationOutcome.Completed : ConversationOutcome.Blocked,
                mode == "wrong-denials" ? ["Write"] : [ClaudePlanExitProbe.Tool]);
        }
        private ClaudeToolResult Result(string id) => new(id, ClaudePlanExitProbe.Tool, mode != "allowed-result",
            mode == "wrong-result" ? "NATIVE_PRIVATE" : "Ação recusada pelo Sintonia. Não repita nem contorne a recusa.", mode == "truncated");
    }
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sintonia-plan-exit-" + Guid.NewGuid());
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
