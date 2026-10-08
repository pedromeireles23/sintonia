using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class SchedulingTests
{
    [Theory]
    [InlineData(WorkTaskState.Queued)]
    [InlineData(WorkTaskState.Running)]
    [InlineData(WorkTaskState.InReview)]
    [InlineData(WorkTaskState.Failed)]
    [InlineData(WorkTaskState.Cancelled)]
    public void DependencyRequiresApproval(WorkTaskState dependencyState)
    {
        TaskSnapshot[] tasks = [Snapshot("a", ProviderKind.Codex, dependencyState),
            Snapshot("b", ProviderKind.Claude, WorkTaskState.Queued, "a")];
        Assert.DoesNotContain("b", SchedulingPolicy.SelectReady(tasks, new(2, 1, 1)));
    }

    [Fact]
    public void ApprovedDependencyAndProviderSlotsAreRespected()
    {
        TaskSnapshot[] tasks = [Snapshot("a", ProviderKind.Codex, WorkTaskState.Completed),
            Snapshot("b", ProviderKind.Codex, WorkTaskState.Running),
            Snapshot("c", ProviderKind.Codex, WorkTaskState.Queued, "a"),
            Snapshot("d", ProviderKind.Claude, WorkTaskState.Queued, "a"),
            Snapshot("e", ProviderKind.Claude, WorkTaskState.Queued)];
        Assert.Equal(["d"], SchedulingPolicy.SelectReady(tasks, new(2, 1, 1)));
        Assert.Empty(SchedulingPolicy.SelectReady(tasks, new(1, 2, 2)));
    }

    [Theory]
    [InlineData("cycle")]
    [InlineData("self")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    public void InvalidGraphIsRejected(string kind)
    {
        WorkTask[] tasks = kind switch
        {
            "cycle" => [Definition("a", "b"), Definition("b", "a")],
            "self" => [Definition("a", "a")],
            "missing" => [Definition("a", "b")],
            _ => [Definition("a"), Definition("a")]
        };
        Assert.Throws<ArgumentException>(() => SchedulingPolicy.ValidateGraph(tasks));
    }

    [Fact]
    public void InvalidLimitsAreRejected() => Assert.Throws<ArgumentOutOfRangeException>(() => new ConcurrencyLimits(0, 1, 1));

    private static WorkTask Definition(string id, params string[] dependencies) => new(id, id, id, "f", dependencies);
    private static TaskSnapshot Snapshot(string id, ProviderKind provider, WorkTaskState state, params string[] dependencies) =>
        new(Definition(id, dependencies), provider, state, []);
}
