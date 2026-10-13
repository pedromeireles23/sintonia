using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeElicitationProbeTests
{
    private static ExecutableLaunch Fixture(string mode, string root) => new(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), ["claude-no-turn-" + mode, root], "Teste");
    [Fact]
    public async Task ModelFreeControlProofCorrelatesDeclineAndServerAuditWithoutReusingFolder()
    {
        using var folder = new Folder();
        await ClaudeElicitationProbe.RunCaseAsync(folder.Path, Fixture("success", folder.Path), "nonce", "simulation-version", CancellationToken.None);
        ClaudeElicitationProbe.Verify(folder.Path);
        var evidence = File.ReadAllText(Path.Combine(folder.Path, ClaudeElicitationProbe.EvidenceFile));
        Assert.DoesNotContain(LocalElicitationServer.Message, evidence); Assert.DoesNotContain(LocalElicitationServer.Marker, evidence);
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeElicitationProbe.RunCaseAsync(folder.Path, Fixture("success", folder.Path), "nonce", "simulation-version", CancellationToken.None));
        Assert.Equal(evidence, File.ReadAllText(Path.Combine(folder.Path, ClaudeElicitationProbe.EvidenceFile)));
        File.WriteAllText(Path.Combine(folder.Path, LocalElicitationServer.AuditFile), "{}");
        Assert.Throws<ProviderException>(() => ClaudeElicitationProbe.Verify(folder.Path));
    }

    [Theory]
    [InlineData("exit")]
    [InlineData("server")]
    [InlineData("rejected")]
    [InlineData("model")]
    [InlineData("schema")]
    [InlineData("permission")]
    [InlineData("duplicate")]
    [InlineData("id")]
    [InlineData("result")]
    [InlineData("error")]
    [InlineData("content")]
    [InlineData("file")]
    [InlineData("timeout")]
    public async Task DivergencePreservesPartialEvidenceAndNeverStartsAModelOrRetries(string mode)
    {
        using var folder = new Folder(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(mode == "timeout" ? 1 : 8));
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeElicitationProbe.RunCaseAsync(folder.Path, Fixture(mode, folder.Path), "nonce", "simulation-version", timeout.Token));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, ClaudeElicitationProbe.EvidenceFile)));
        Assert.Equal("Interrupted", doc.RootElement.GetProperty("State").GetString());
        Assert.InRange(doc.RootElement.GetProperty("Calls").GetInt32(), 0, 1);
        Assert.Equal(0, doc.RootElement.GetProperty("UserMessagesSent").GetInt32());
        Assert.DoesNotContain("NATIVE_PRIVATE", doc.RootElement.GetRawText());
        if (mode == "model") Assert.Equal(1, doc.RootElement.GetProperty("ModelEvents").GetInt32());
        if (mode == "rejected") Assert.Equal("ControlCallRejected", doc.RootElement.GetProperty("FailureCode").GetString());
    }

    [Fact]
    public void LaunchScopesOneLocalStdioServerWithoutBypassOrExternalTools()
    {
        var launch = ClaudeElicitationProbe.CreateLaunch(new("claude.exe", [], "Claude"), new("C:/test/server.exe", [], "Servidor"), "C:/test/proof", new string('a', 32));
        Assert.Contains("--strict-mcp-config", launch.PrefixArguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", launch.PrefixArguments);
        using var config = JsonDocument.Parse(launch.PrefixArguments[1]);
        var server = Assert.Single(config.RootElement.GetProperty("mcpServers").EnumerateObject());
        Assert.Equal(LocalElicitationServer.Name, server.Name);
        Assert.Equal("C:/test/server.exe", server.Value.GetProperty("command").GetString());
        Assert.Equal("mcp-elicitation-server", server.Value.GetProperty("args")[0].GetString());
    }

    [Theory]
    [InlineData("decline", true, true)]
    [InlineData("accept", true, false)]
    [InlineData("decline", false, false)]
    public async Task LocalServerNegotiatesFormAndRequiresDeclineWithoutData(string action, bool form, bool success)
    {
        using var folder = new Folder(); var nonce = new string('a', 32);
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderProcess? active = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var process = new ProviderProcess(new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"),
            ["mcp-elicitation-server", folder.Path, nonce], "Servidor de teste"), [], folder.Path, Receive);
        active = process;
        await process.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
        { protocolVersion = "2025-11-25", capabilities = form ? new Dictionary<string, object> { ["elicitation"] = new { form = new { } } } : [] } }), timeout.Token);
        await received.Task.WaitAsync(timeout.Token);
        await process.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method = "notifications/initialized" }), timeout.Token);
        await process.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 2, method = "tools/call", @params = new
        { name = LocalElicitationServer.Tool, arguments = new { nonce } } }), timeout.Token);
        if (success) { await completion.Task.WaitAsync(timeout.Token); process.CloseInput(); }
        await process.Completion.WaitAsync(timeout.Token);
        using var audit = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder.Path, LocalElicitationServer.AuditFile)));
        Assert.Equal(success ? "Completed" : "Interrupted", audit.RootElement.GetProperty("State").GetString());
        Assert.Equal(success ? 0 : 2, process.ExitCode);

        async ValueTask Receive(string line)
        {
            using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
            if (message.TryGetProperty("method", out var method))
            {
                Assert.Equal("elicitation/create", method.GetString());
                await active!.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "sintonia-form", result = new { action } }), timeout.Token);
            }
            else if (message.GetProperty("id").GetInt32() == 1) received.TrySetResult();
            else { Assert.Contains(LocalElicitationServer.Marker, message.GetRawText()); completion.TrySetResult(); }
        }
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sintonia-elicitation-" + Guid.NewGuid());
        public Folder() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
