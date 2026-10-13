using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ProviderUsageTests
{
    private static ExecutableLaunch Fixture(string scenario) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [scenario], "Teste");
    private static ProviderUsageSnapshot Parse(string json) => CodexUsageParser.Parse(JsonSerializer.Deserialize<JsonElement>(json), DateTimeOffset.UtcNow);
    [Fact]
    public void MultiBucketIsAuthoritativeAndMissingDataNeverBecomesZeroOrLegacyAvailability()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":1}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":25,"windowDurationMins":300,"resetsAt":1893456000},"secondary":null},"other":{}},"ordinaryUsageAllowed":null,"accountId":"SEGREDO","credits":{"balance":"SEGREDO"}}""");
        Assert.Equal(2, snapshot.Buckets.Count); Assert.Equal(25, snapshot.Buckets[0].Primary!.UsedPercent); Assert.Equal(75, snapshot.Buckets[0].Primary!.RemainingPercent);
        Assert.Equal(300, snapshot.Buckets[0].Primary!.DurationMinutes); Assert.NotNull(snapshot.Buckets[0].Primary!.ResetsAt); Assert.Null(snapshot.Buckets[0].Secondary);
        Assert.Null(snapshot.Buckets[1].Primary); Assert.Null(snapshot.OrdinaryUsageAllowed); Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
        snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":1}},"rateLimitsByLimitId":{}}""");
        Assert.Empty(snapshot.Buckets); Assert.False(snapshot.HasData); Assert.NotNull(snapshot.UnavailableReason);
    }
    [Fact]
    public void LegacyAndUnavailableFieldsRemainDistinctFromKnownAllowanceAndExhaustedWindows()
    {
        var snapshot = Parse("""{"rateLimits":{"limitId":null,"primary":{"usedPercent":135,"windowDurationMins":null,"resetsAt":null}},"ordinaryUsageAllowed":true}""");
        Assert.Equal(0, snapshot.Buckets.Single().Primary!.RemainingPercent); Assert.True(snapshot.OrdinaryUsageAllowed); Assert.Null(snapshot.Buckets.Single().Primary!.ResetsAt);
        snapshot = Parse("""{"rateLimits":{},"ordinaryUsageAllowed":false}"""); Assert.True(snapshot.HasData); Assert.False(snapshot.OrdinaryUsageAllowed);
        snapshot = Parse("""{"rateLimits":{}}"""); Assert.False(snapshot.HasData); Assert.Null(snapshot.OrdinaryUsageAllowed);
    }
    [Theory]
    [InlineData("{\"ordinaryUsageAllowed\":\"true\"}")]
    [InlineData("{\"rateLimitsByLimitId\":[],\"rateLimits\":{\"primary\":{\"usedPercent\":25}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":-1}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":\"25\"}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"resetsAt\":9223372036854775807}}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"usedPercent\":25,\"windowDurationMins\":0}}}")]
    public void MalformedMetadataIsNotReportedAsKnownUsage(string json) => Assert.Throws<FormatException>(() => Parse(json));
    [Fact]
    public async Task LiveProtocolReadsOnlyAccountMetadataAndDisposesWithoutCreatingThread()
    {
        using var directory = new DirectoryFixture(); var snapshot = await new CodexConversationProvider(Fixture("rpc-usage-success")).ReadUsageAsync(directory.Path, CancellationToken.None);
        Assert.True(snapshot.HasData); Assert.True(snapshot.OrdinaryUsageAllowed); Assert.Equal(25, snapshot.Buckets.Single().Primary!.UsedPercent);
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "thread-started.txt"))); Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
    }
    [Theory]
    [InlineData("rpc-usage-denied")]
    [InlineData("rpc-usage-denied-invalid")]
    public async Task ExplicitDenialStopsBeforeThreadAndTurnRegardlessOfPercentages(string scenario)
    {
        using var directory = new DirectoryFixture(); var provider = new CodexConversationProvider(Fixture(scenario));
        var error = await Assert.ThrowsAsync<ProviderException>(() => provider.SendAsync(new(directory.Path, "SIMULAÇÃO"), new SilentProgress(), CancellationToken.None));
        Assert.Contains("uso incluído está indisponível", error.Message); Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "thread-started.txt")));
    }
    [Theory]
    [InlineData("rpc-usage-full-allowed")]
    [InlineData("rpc-usage-unknown")]
    [InlineData("rpc-usage-unsupported")]
    public async Task PercentagesMissingAndUnsupportedDataNeverInventAnAdmissionDecision(string scenario)
    {
        using var directory = new DirectoryFixture(); var result = await new CodexConversationProvider(Fixture(scenario)).SendAsync(new(directory.Path, "SIMULAÇÃO"), new SilentProgress(), CancellationToken.None);
        Assert.Equal(ConversationOutcome.Completed, result.Outcome); Assert.True(File.Exists(System.IO.Path.Combine(directory.Path, "thread-started.txt")));
    }
    [Theory]
    [InlineData("rpc-usage-unsupported")]
    [InlineData("rpc-usage-invalid")]
    [InlineData("rpc-api")]
    public async Task QueryFailuresAreGenericAndDoNotExposeRawServerDataOrStartInference(string scenario)
    {
        using var directory = new DirectoryFixture(); var error = await Assert.ThrowsAsync<ProviderException>(() => new CodexConversationProvider(Fixture(scenario)).ReadUsageAsync(directory.Path, CancellationToken.None));
        Assert.Contains("nenhum turno", error.Message); Assert.DoesNotContain("SEGREDO", error.Message); Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "thread-started.txt")));
    }
    [Fact]
    public async Task CancellationStopsWaitingQueryWithoutStartingInference()
    {
        using var directory = new DirectoryFixture(); using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CodexConversationProvider(Fixture("rpc-usage-wait")).ReadUsageAsync(directory.Path, stop.Token));
        Assert.False(File.Exists(System.IO.Path.Combine(directory.Path, "thread-started.txt")));
    }

    [Fact]
    public async Task ClaudeUsageProjectsOnlyPlanWindowsFromGetUsageWithoutATurn()
    {
        using var directory = new DirectoryFixture(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var snapshot = await new ClaudeConversationProvider(Fixture("claude-usage")).ReadUsageAsync(directory.Path, stop.Token);
        Assert.Equal(ProviderKind.Claude, snapshot.Provider); Assert.Null(snapshot.OrdinaryUsageAllowed);
        Assert.Equal(new[] { "claude", "seven_day_opus" }, snapshot.Buckets.Select(b => b.Id));
        var (session, weekly) = snapshot.PlanWindows();
        Assert.Equal((4, 96, 300L), (session!.UsedPercent, session.RemainingPercent, session.DurationMinutes!.Value));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 2, 29, 59, TimeSpan.Zero), new DateTimeOffset(session.ResetsAt!.Value.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero));
        Assert.Equal((6, 10080L), (weekly!.UsedPercent, weekly.DurationMinutes!.Value));
        Assert.Equal(40, snapshot.Buckets[1].Secondary!.UsedPercent); Assert.Null(snapshot.Buckets[1].Primary);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot)); Assert.DoesNotContain("777", JsonSerializer.Serialize(snapshot));
        var unavailable = await new ClaudeConversationProvider(Fixture("claude-usage-unavailable")).ReadUsageAsync(directory.Path, stop.Token);
        Assert.False(unavailable.HasData); Assert.Contains("não estão disponíveis", unavailable.UnavailableReason);
    }

    [Theory]
    [InlineData("claude-usage-unsupported", "Atualize o CLI")]
    [InlineData("claude-usage-invalid", "formato incompatível")]
    public async Task ClaudeUsageFailuresAreSafeAndNeverSendAPrompt(string scenario, string message)
    {
        using var directory = new DirectoryFixture(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<ProviderException>(() => new ClaudeConversationProvider(Fixture(scenario)).ReadUsageAsync(directory.Path, stop.Token));
        Assert.Contains(message, error.Message); Assert.Contains("nenhum turno", error.Message); Assert.DoesNotContain("SEGREDO", error.Message);
    }
    private sealed class SilentProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    private sealed class DirectoryFixture : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sintonia-usage-" + Guid.NewGuid());
        public DirectoryFixture() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            var target = System.IO.Path.GetFullPath(Path);
            if (!target.StartsWith(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) || !System.IO.Path.GetFileName(target).StartsWith("sintonia-usage-")) throw new InvalidOperationException();
            Directory.Delete(target, true);
        }
    }
}
