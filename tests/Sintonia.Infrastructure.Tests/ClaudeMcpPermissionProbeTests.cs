using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeMcpPermissionProbeTests
{
    [Fact]
    public async Task FixedProofUsesTwoDecisionsSameSessionAndReadOnlyVerifier()
    {
        using var folder = new Folder(); var provider = new Simulation();
        await ClaudeMcpPermissionProbe.RunCaseAsync(folder.Path, provider.Create, CancellationToken.None);
        ClaudeMcpPermissionProbe.Verify(folder.Path);
        Assert.Equal(2, provider.Calls); Assert.Equal(new[] { false, true }, provider.Decisions);
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeMcpPermissionProbe.RunCaseAsync(folder.Path, provider.Create, CancellationToken.None));
        Assert.Equal(2, provider.Calls);
        var path = System.IO.Path.Combine(folder.Path, "mcp-evidence.json");
        Assert.DoesNotContain("OperationCanceledException", File.ReadAllText(path)); // Only projected evidence, never response body.
        File.WriteAllText(path, File.ReadAllText(path).Replace("Completed", "Interrupted", StringComparison.Ordinal));
        Assert.Throws<ProviderException>(() => ClaudeMcpPermissionProbe.Verify(folder.Path));
    }

    [Theory]
    [InlineData("missing-host", 1)]
    [InlineData("wrong-input", 1)]
    [InlineData("duplicate-request", 1)]
    [InlineData("deny-success", 1)]
    [InlineData("deny-body", 1)]
    [InlineData("missing-result", 2)]
    [InlineData("error-result", 2)]
    [InlineData("wrong-page", 2)]
    [InlineData("truncated", 2)]
    [InlineData("extra-tool", 2)]
    [InlineData("wrong-session", 2)]
    public async Task InvalidHostOrToolEvidenceStopsWithoutRetry(string mode, int calls)
    {
        using var folder = new Folder(); var provider = new Simulation(mode);
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeMcpPermissionProbe.RunCaseAsync(folder.Path, provider.Create, CancellationToken.None));
        Assert.Equal(calls, provider.Calls);
        using var doc = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(folder.Path, "mcp-evidence.json")));
        Assert.Equal("Interrupted", doc.RootElement.GetProperty("State").GetString());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"url\":\"https://example.com\"}")]
    [InlineData("{\"url\":123}")]
    [InlineData("{\"url\":\"https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation\",\"extra\":true}")]
    [InlineData("{\"url\":\"https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/task-cancellation\",\"url\":\"https://example.com\"}")]
    [InlineData("not JSON")]
    public void AuthorizationRejectsDifferentUrlDuplicateOrUnknownParameters(string json)
    {
        Assert.False(ClaudeMcpPermissionProbe.Matches(Permission(AppContext.BaseDirectory, json), AppContext.BaseDirectory));
    }

    private static ConversationPermission Permission(string folder, string json) => new(ProviderKind.Claude,
        ClaudeMcpPermissionProbe.Tool, "Claude · MCP\n\n" + json, folder);
    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sintonia-MCP-test-" + Guid.NewGuid());
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Callback and stream simulation only: no network, process or model.</summary>
    private sealed class Simulation(string mode = "") : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public int Calls { get; private set; }
        public List<bool> Decisions { get; } = [];
        private Action<ClaudeToolResult> _observe = _ => { };
        public IConversationProvider Create(Action<ClaudeToolResult> observe) { _observe = observe; return this; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++;
            Assert.Equal(ClaudeMcpPermissionProbe.Prompt, request.Prompt);
            if (Calls == 2) { Assert.Equal("simulation-session", request.NativeSessionId); Assert.Equal("simulation-model", request.Model); }
            var input = mode == "wrong-input" ? "{}" : JsonSerializer.Serialize(new { url = ClaudeMcpPermissionProbe.Page });
            var permission = Permission(request.WorkingDirectory, input);
            var approved = mode != "missing-host" && await request.PermissionHandler!(permission, token);
            Decisions.Add(approved);
            if (mode == "duplicate-request") Decisions.Add(await request.PermissionHandler!(permission, token));
            progress.Report(new(ConversationEventKind.Tool, ClaudeMcpPermissionProbe.Tool));
            if (Calls == 2 && mode == "extra-tool") progress.Report(new(ConversationEventKind.Tool, "different-tool"));
            if (!(Calls == 2 && mode == "missing-result"))
                _observe(new("simulation-call-" + Calls, ClaudeMcpPermissionProbe.Tool,
                    Calls == 2 ? mode == "error-result" : mode != "deny-success",
                    approved || mode == "deny-body" ? mode == "wrong-page" ? "SIMULAÇÃO: outra página" : "SIMULAÇÃO: Task cancellation CancellationTokenSource OperationCanceledException" : "SIMULAÇÃO: recusado",
                    mode == "truncated" && Calls == 2));
            return new(mode == "wrong-session" && Calls == 2 ? "different-session" : request.NativeSessionId ?? "simulation-session",
                "simulation-model", "SIMULAÇÃO", approved ? ConversationOutcome.Completed : ConversationOutcome.Blocked,
                approved ? [] : [ClaudeMcpPermissionProbe.Tool]);
        }
    }
}
