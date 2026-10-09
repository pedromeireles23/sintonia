using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeToolPermissionProbeTests
{
    [Theory]
    [InlineData("Bash")]
    [InlineData("Edit")]
    public async Task FixedProofDeniesThenAllowsAndResumesSameSession(string tool)
    {
        using var directory = new DirectoryFixture();
        var provider = new SimulatedProvider();
        await ClaudeToolPermissionProbe.RunCaseAsync(directory.Path, tool, provider, CancellationToken.None);
        Assert.Equal(2, provider.Calls); Assert.Equal(new[] { false, true }, provider.Decisions);
        Assert.True(provider.Resumed);
        Assert.True(File.Exists(Path.Combine(directory.Path, tool, "permitido.txt")));
        // Reusing a partial or finished case never sends another prompt.
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeToolPermissionProbe.RunCaseAsync(directory.Path, tool, provider, CancellationToken.None));
        Assert.Equal(2, provider.Calls);
    }

    [Theory]
    [InlineData("Bash", "effect-after-denial")]
    [InlineData("Edit", "effect-after-denial")]
    [InlineData("Bash", "wrong-input")]
    [InlineData("Edit", "wrong-input")]
    [InlineData("Bash", "duplicate-request")]
    [InlineData("Edit", "duplicate-request")]
    public async Task FailedDenialStopsBeforeAnotherTurnAndPreservesEvidence(string tool, string mode)
    {
        using var directory = new DirectoryFixture(); var provider = new SimulatedProvider(mode);
        await Assert.ThrowsAsync<ProviderException>(() => ClaudeToolPermissionProbe.RunCaseAsync(directory.Path, tool, provider, CancellationToken.None));
        Assert.Equal(1, provider.Calls);
        Assert.DoesNotContain(true, provider.Decisions);
        using var evidence = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, tool + "-evidence.json")));
        Assert.Equal("Interrupted", evidence.RootElement.GetProperty("State").GetString());
    }

    [Theory]
    [InlineData("run_in_background", "true")]
    [InlineData("dangerouslyDisableSandbox", "true")]
    [InlineData("timeout", "10001")]
    [InlineData("timeout", "\"1000\"")]
    [InlineData("cwd", "\"elsewhere\"")]
    public void BashRejectsBackgroundUnsafeFlagsTimeoutOrAdditionalExecutionParameters(string field, string value)
    {
        var spec = new ClaudeToolPermissionProbe.ActionSpec("Bash", AppContext.BaseDirectory, "permitido.txt", "marker", "original");
        var input = JsonSerializer.SerializeToElement(spec.Input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        input[field] = JsonSerializer.Deserialize<JsonElement>(value);
        Assert.False(ClaudeToolPermissionProbe.Matches(Permission(spec, JsonSerializer.Serialize(input)), spec));
    }

    [Theory]
    [InlineData("file_path", "\"../elsewhere.txt\"")]
    [InlineData("old_string", "\"different\"")]
    [InlineData("new_string", "\"different\"")]
    [InlineData("replace_all", "true")]
    public void EditRejectsAnotherTargetOrReplacement(string field, string value)
    {
        var spec = new ClaudeToolPermissionProbe.ActionSpec("Edit", AppContext.BaseDirectory, "permitido.txt", "marker", "original");
        var input = JsonSerializer.SerializeToElement(spec.Input).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        input[field] = JsonSerializer.Deserialize<JsonElement>(value);
        Assert.False(ClaudeToolPermissionProbe.Matches(Permission(spec, JsonSerializer.Serialize(input)), spec));
    }

    [Fact]
    public async Task FileVerificationDoesNotSendPromptsAndRejectsChangedArtifacts()
    {
        using var directory = new DirectoryFixture(); var provider = new SimulatedProvider();
        await ClaudeToolPermissionProbe.RunCaseAsync(directory.Path, "Bash", provider, CancellationToken.None);
        await ClaudeToolPermissionProbe.RunCaseAsync(directory.Path, "Edit", provider, CancellationToken.None);
        ClaudeToolPermissionProbe.VerifyFiles(directory.Path);
        File.AppendAllText(Path.Combine(directory.Path, "Edit", "negado.txt"), "changed");
        Assert.Throws<ProviderException>(() => ClaudeToolPermissionProbe.VerifyFiles(directory.Path));
        Assert.Equal(4, provider.Calls);
    }

    private static ConversationPermission Permission(ClaudeToolPermissionProbe.ActionSpec spec, string input) =>
        new(ProviderKind.Claude, spec.Tool, $"Claude · {spec.Tool}\n\n{input}", spec.Directory);

    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Sintonia-probe-" + Guid.NewGuid());
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>Only simulated file effects after the callback; no process, shell, network or model.</summary>
    private sealed class SimulatedProvider(string mode = "") : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public int Calls { get; private set; }
        public bool Resumed { get; private set; }
        public List<bool> Decisions { get; } = [];

        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++;
            var tool = request.Prompt.Contains("para Bash:", StringComparison.Ordinal) ? "Bash" : "Edit";
            var jsonStart = request.Prompt.IndexOf('\n') + 1;
            var jsonEnd = request.Prompt.IndexOf('\n', jsonStart);
            var json = request.Prompt[jsonStart..jsonEnd];
            using var document = JsonDocument.Parse(json);
            var input = document.RootElement;
            var preview = mode == "wrong-input" ? "{}" : json;
            var permission = new ConversationPermission(Kind, tool, $"Claude · {tool}\n\n{preview}", request.WorkingDirectory);
            var approved = await request.PermissionHandler!(permission, token); Decisions.Add(approved);
            if (mode == "duplicate-request") Decisions.Add(await request.PermissionHandler(permission, token));
            if (approved || mode == "effect-after-denial")
            {
                string file; string marker;
                if (tool == "Bash")
                {
                    var command = input.GetProperty("command").GetString()!;
                    file = Path.Combine(request.WorkingDirectory, Regex.Match(command, @"Path\('([^']+)'\)").Groups[1].Value);
                    marker = Regex.Match(command, @"write_text\('([^']+)'", RegexOptions.CultureInvariant).Groups[1].Value;
                }
                else { file = input.GetProperty("file_path").GetString()!; marker = input.GetProperty("new_string").GetString()!; }
                await File.WriteAllTextAsync(file, marker, new UTF8Encoding(false), token);
            }
            Resumed |= request.NativeSessionId is not null && request.Model == "fixture-model";
            return new(request.NativeSessionId ?? "fixture-session", "fixture-model", "SIMULAÇÃO",
                approved ? ConversationOutcome.Completed : ConversationOutcome.Blocked, approved ? [] : [tool]);
        }
    }
}
