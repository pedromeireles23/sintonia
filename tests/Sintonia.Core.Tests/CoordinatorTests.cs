using System.Collections.Concurrent;
using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class CoordinatorTests
{
    [Fact]
    public async Task ConcurrentDispatchReservesEachTaskOnceAndRequiresExplicitApproval()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex);
        var claude = new ControlledAdapter(ProviderKind.Claude);
        var coordinator = Create(codex, claude, Task("a"), Task("b", "claude", "a"));
        var dispatches = Enumerable.Range(0, 12).Select(_ => coordinator.DispatchAsync()).ToArray();
        Assert.Single(codex.Requests);
        codex.Complete("a");
        await System.Threading.Tasks.Task.WhenAll(dispatches).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkTaskState.InReview, coordinator.Snapshot.Tasks[0].State);
        Assert.Empty(claude.Requests);
        await coordinator.DispatchAsync();
        Assert.Empty(claude.Requests);
        coordinator.Approve("a");
        var dependent = coordinator.DispatchAsync();
        var delivery = Assert.Single(Assert.Single(claude.Requests).Dependencies);
        Assert.Equal(coordinator.Snapshot.Tasks[0].LatestRun!.Id, delivery.RunId);
        Assert.Equal("Entrega a", delivery.Summary);
        claude.Complete("b");
        await dependent.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkTaskState.InReview, coordinator.Snapshot.Tasks[1].State);
    }

    [Fact]
    public async Task FreeSlotIsRefilledWhileOtherProviderStillRuns()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex);
        var claude = new ControlledAdapter(ProviderKind.Claude);
        var coordinator = Create(codex, claude, Task("a"), Task("b", "claude"), Task("c"));
        var dispatch = coordinator.DispatchAsync();
        var cStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        codex.Started = id => { if (id == "c") cStarted.TrySetResult(); };
        codex.Complete("a");
        await cStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkTaskState.Running, coordinator.Snapshot.Tasks[1].State);
        codex.Complete("c");
        claude.Complete("b");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CancelledAdapterKeepsSlotUntilItActuallyStopsAndLateSuccessIsIgnored()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex) { IgnoreCancellation = true };
        var claude = new ControlledAdapter(ProviderKind.Claude);
        var coordinator = Create(codex, claude, Task("a"), Task("b"));
        var dispatch = coordinator.DispatchAsync();
        coordinator.Cancel("a");
        Assert.Throws<InvalidOperationException>(() => coordinator.Retry("a"));
        await coordinator.DispatchAsync();
        Assert.Single(codex.Requests);
        var bStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        codex.Started = id => { if (id == "b") bStarted.TrySetResult(); };
        codex.Complete("a");
        await bStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        codex.Complete("b");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelled = coordinator.Snapshot.Tasks[0];
        Assert.Equal(WorkTaskState.Cancelled, cancelled.State);
        Assert.Equal(RunState.Cancelled, cancelled.LatestRun!.State);
        Assert.Null(cancelled.LatestRun.Result);
    }

    [Fact]
    public async Task FailureDoesNotReleaseDependenciesAndRetryPreservesHistory()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex);
        var claude = new ControlledAdapter(ProviderKind.Claude);
        var coordinator = Create(codex, claude, Task("a"), Task("b", "claude", "a"));
        var dispatch = coordinator.DispatchAsync();
        codex.Fail("a");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkTaskState.Failed, coordinator.Snapshot.Tasks[0].State);
        Assert.Empty(claude.Requests);
        Assert.Throws<InvalidOperationException>(() => coordinator.Approve("a"));
        coordinator.Retry("a");
        codex.Reset("a");
        var retry = coordinator.DispatchAsync();
        codex.Complete("a");
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        var runs = coordinator.Snapshot.Tasks[0].Runs;
        Assert.Equal(2, runs.Count);
        Assert.Equal(RunState.Failed, runs[0].State);
        Assert.Equal(RunState.Succeeded, runs[1].State);
        Assert.NotEqual(runs[0].Session.Id, runs[1].Session.Id);
        Assert.Empty(claude.Requests);
    }

    [Fact]
    public async Task CancellationAndReviewRejectionCanReturnToQueue()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex);
        var coordinator = Create(codex, new(ProviderKind.Claude), Task("a"));
        var dispatch = coordinator.DispatchAsync();
        coordinator.Cancel("a");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(RunState.Cancelled, coordinator.Snapshot.Tasks[0].LatestRun!.State);
        coordinator.Retry("a");
        codex.Reset("a");
        var retry = coordinator.DispatchAsync();
        codex.Complete("a");
        await retry.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Retry("a");
        Assert.Equal(WorkTaskState.Queued, coordinator.Snapshot.Tasks[0].State);
        Assert.Equal(2, coordinator.Snapshot.Tasks[0].Runs.Count);
        coordinator.Cancel("a");
        Assert.Equal(WorkTaskState.Cancelled, coordinator.Snapshot.Tasks[0].State);
    }

    [Fact]
    public async Task ProviderAssignmentChangesQueuedWorkButNeverMutatesRunningAttempts()
    {
        var codex = new ControlledAdapter(ProviderKind.Codex);
        var claude = new ControlledAdapter(ProviderKind.Claude);
        var coordinator = Create(codex, claude, Task("a"));
        coordinator.AssignProvider("codex", ProviderKind.Claude);
        var dispatch = coordinator.DispatchAsync();
        Assert.Empty(codex.Requests);
        Assert.Throws<InvalidOperationException>(() => coordinator.AssignProvider("codex", ProviderKind.Codex));
        claude.Complete("a");
        await dispatch.WaitAsync(TimeSpan.FromSeconds(5));
        coordinator.Approve("a");
        Assert.Throws<InvalidOperationException>(() => coordinator.Retry("a"));
    }

    private static WorkTask Task(string id, string function = "codex", params string[] dependencies) => new(id, id, id, function, dependencies);
    private static TaskCoordinator Create(ControlledAdapter codex, ControlledAdapter claude, params WorkTask[] tasks) =>
        new([new("codex", "Gameplay", "Consigne", ProviderKind.Codex), new("claude", "Interface", "Consigne", ProviderKind.Claude)],
            tasks, [codex, claude], new(2, 1, 1));

    private sealed class ControlledAdapter(ProviderKind kind) : IProviderAdapter
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<ProviderResult>> _results = new();
        public ProviderKind Kind => kind;
        public bool IsSimulated => true;
        public bool IgnoreCancellation { get; init; }
        public Action<string>? Started { get; set; }
        public ConcurrentQueue<ProviderRequest> Requests { get; } = new();
        public async Task<ProviderResult> ExecuteAsync(ProviderRequest request, IProgress<ProviderEvent> progress, CancellationToken cancellationToken)
        {
            var result = _results.GetOrAdd(request.Task.Id, _ => NewSource());
            Requests.Enqueue(request);
            progress.Report(new(ProviderEventKind.Message, "Iniciado"));
            Started?.Invoke(request.Task.Id);
            return await (IgnoreCancellation ? result.Task : result.Task.WaitAsync(cancellationToken));
        }
        public void Complete(string id) => _results[id].SetResult(new($"Entrega {id}"));
        public void Fail(string id) => _results[id].SetException(new InvalidOperationException("Falha controlada"));
        public void Reset(string id) => _results[id] = NewSource();
        private static TaskCompletionSource<ProviderResult> NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
