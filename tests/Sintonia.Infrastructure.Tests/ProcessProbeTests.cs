using System.Diagnostics;
using System.Text.Json;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Tests;

public sealed class ProcessProbeTests
{
    private static ExecutableLaunch Fixture => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [], "Teste");

    [Fact]
    public async Task ArgumentsStayLiteralIncludingSpacesAccentsAndShellSyntax()
    {
        string[] literals = ["pasta com espaços", "ação", "$(not-a-command)", "`backtick`", "&|;%PATH%"];
        var result = await ProcessProbe.RunAsync(Fixture, new[] { "echo" }.Concat(literals).ToArray(),
            AppContext.BaseDirectory, TimeSpan.FromSeconds(5));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(literals, JsonSerializer.Deserialize<string[]>(result.StandardOutput));
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task BothPipesDrainWithoutDeadlockAndRetainedOutputIsBounded()
    {
        var result = await ProcessProbe.RunAsync(Fixture, ["flood"], AppContext.BaseDirectory, TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Truncated);
        Assert.Equal(65536, result.StandardOutput.Length);
        Assert.Equal(65536, result.StandardError.Length);
    }

    [Fact]
    public async Task NonzeroExitAndStderrArePreserved()
    {
        var result = await ProcessProbe.RunAsync(Fixture, ["fail"], AppContext.BaseDirectory, TimeSpan.FromSeconds(5));
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("Falha de teste", result.StandardError);
    }

    [Fact]
    public async Task TimeoutKillsTheProcessAndItsChild()
    {
        var result = await ProcessProbe.RunAsync(Fixture, ["child"], AppContext.BaseDirectory, TimeSpan.FromSeconds(2));
        Assert.True(result.TimedOut);
        var childLine = result.StandardOutput.Split('\n').Single(s => s.StartsWith("CHILD:", StringComparison.Ordinal));
        var childId = int.Parse(childLine[6..]);
        try
        {
            using var child = Process.GetProcessById(childId);
            Assert.True(child.HasExited || child.WaitForExit(3000), "O processo filho continua executando.");
        }
        catch (ArgumentException) { /* The OS already removed the terminated child. */ }
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsSuccessOrTimeout()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessProbe.RunAsync(Fixture,
            ["wait"], AppContext.BaseDirectory, TimeSpan.FromSeconds(5), cancellation.Token));
    }
}
