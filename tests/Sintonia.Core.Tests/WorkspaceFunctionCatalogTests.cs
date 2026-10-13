using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class WorkspaceFunctionCatalogTests
{
    [Fact]
    public void EveryFunctionHasASummaryAndStarterProfilesAreValidAndFilled()
    {
        Assert.All(WorkspaceFunctionCatalog.Functions, f => Assert.False(string.IsNullOrWhiteSpace(f.Summary)));
        Assert.Equal(WorkspaceFunctionCatalog.Functions.Count, WorkspaceFunctionCatalog.Functions.Select(f => f.Name).Distinct().Count());
        var starters = WorkspaceFunctionCatalog.StarterProfiles();
        Assert.DoesNotContain(starters, p => p.FunctionName is "Conversa" or WorkspaceFunctionCatalog.CustomFunctionName);
        Assert.Contains(starters, p => p.FunctionName == PlanProposalFormat.ChiefFunctionName);
        Assert.All(starters, p =>
        {
            Assert.Equal(p, WorkspaceFunctionProfiles.Normalize(p));
            Assert.False(string.IsNullOrWhiteSpace(p.Instructions));
            Assert.Null(p.Model);
        });
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Instruções escritas pelo usuário.", false)]
    public void OnlyEmptyOrBuiltInInstructionsAreReplacedWhenSwitchingFunction(string text, bool replaceable)
    {
        Assert.Equal(replaceable, WorkspaceFunctionCatalog.IsReplaceable(text));
        Assert.True(WorkspaceFunctionCatalog.IsReplaceable(WorkspaceFunctionCatalog.Find("Revisão")!.Instructions));
        Assert.Null(WorkspaceFunctionCatalog.Find("Função inventada"));
    }

    [Fact]
    public void PlanWindowsSeparateShortAndWeeklyWindowsByDuration()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2);
        var swapped = new ProviderUsageSnapshot(ProviderKind.Codex, DateTimeOffset.UtcNow,
            [new("vazio", null, null, null), new("codex", null, new(16, 10080, reset), new(3, 300, reset))], true);
        var (session, weekly) = swapped.PlanWindows();
        Assert.Equal((3, 16), (session!.UsedPercent, weekly!.UsedPercent));
        var withoutDuration = swapped with { Buckets = [new("codex", null, new(10, null, null), new(20, null, null))] };
        Assert.Equal((10, 20), (withoutDuration.PlanWindows().Session!.UsedPercent, withoutDuration.PlanWindows().Weekly!.UsedPercent));
        var weeklyOnly = swapped with { Buckets = [new("codex", null, null, new(50, 10080, null))] };
        Assert.Equal((null, 50), (weeklyOnly.PlanWindows().Session?.UsedPercent, weeklyOnly.PlanWindows().Weekly?.UsedPercent));
        Assert.Equal((null, null), (ProviderUsageSnapshot.Unavailable(ProviderKind.Claude, "sem dados").PlanWindows().Session, ProviderUsageSnapshot.Unavailable(ProviderKind.Claude, "sem dados").PlanWindows().Weekly));
    }
}
