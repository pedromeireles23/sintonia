namespace Sintonia.Core;

/// <summary>Owns reservations and transitions. Provider work and notifications run outside the lock.</summary>
public sealed class TaskCoordinator
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FunctionProfile> _functions;
    private readonly Dictionary<string, Entry> _tasks;
    private readonly Dictionary<ProviderKind, IProviderAdapter> _adapters;
    private readonly ConcurrencyLimits _limits;
    public event Action? Changed;

    public TaskCoordinator(IEnumerable<FunctionProfile> functions, IEnumerable<WorkTask> tasks,
        IEnumerable<IProviderAdapter> adapters, ConcurrencyLimits limits)
    {
        _functions = functions.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var definitions = tasks.Select(t => t with { DependencyIds = Array.AsReadOnly(t.DependencyIds.ToArray()) }).ToArray();
        SchedulingPolicy.ValidateGraph(definitions);
        if (definitions.Any(t => !_functions.ContainsKey(t.FunctionId)))
            throw new ArgumentException("Uma tarefa referencia uma função desconhecida.", nameof(tasks));
        _tasks = definitions.ToDictionary(t => t.Id, t => new Entry(t), StringComparer.Ordinal);
        _adapters = adapters.ToDictionary(a => a.Kind);
        if (Enum.GetValues<ProviderKind>().Any(p => !_adapters.ContainsKey(p)))
            throw new ArgumentException("Configure um adaptador para cada provedor.", nameof(adapters));
        _limits = limits;
    }

    public CoordinatorSnapshot Snapshot
    {
        get { lock (_gate) return SnapshotUnsafe(); }
    }

    public void AssignProvider(string functionId, ProviderKind provider)
    {
        lock (_gate)
        {
            if (!_adapters.ContainsKey(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
            if (_tasks.Values.Any(t => t.Definition.FunctionId == functionId && t.Cancellation is not null))
                throw new InvalidOperationException("Aguarde a execução desta função terminar para trocar o provedor.");
            _functions[functionId] = _functions[functionId] with { Provider = provider };
        }
        Changed?.Invoke();
    }

    public async Task DispatchAsync()
    {
        List<Reservation> reservations = [];
        lock (_gate)
        {
            foreach (var id in SchedulingPolicy.SelectReady(SnapshotUnsafe().Tasks, _limits))
            {
                var entry = _tasks[id];
                var function = _functions[entry.Definition.FunctionId];
                var session = new ProviderSession(Guid.NewGuid().ToString("N"), function.Provider,
                    function.Id, _adapters[function.Provider].IsSimulated);
                var run = new TaskRun(Guid.NewGuid().ToString("N"), session, entry.Runs.Count + 1,
                    DateTimeOffset.UtcNow, null, RunState.Running, null, null, Array.Empty<ProviderEvent>());
                entry.State = WorkTaskState.Running;
                entry.Runs.Add(run);
                entry.Cancellation = new CancellationTokenSource();
                var dependencies = entry.Definition.DependencyIds.Select(d =>
                {
                    var approved = _tasks[d].Runs[^1];
                    return new ApprovedDelivery(d, approved.Id, approved.Result!);
                }).ToArray();
                reservations.Add(new(entry, run, new(entry.Definition, function, session,
                    Array.AsReadOnly(dependencies)), entry.Cancellation));
            }
        }
        if (reservations.Count == 0) return;
        Changed?.Invoke();
        await Task.WhenAll(reservations.Select(ExecuteAsync)).ConfigureAwait(false);
    }

    public void Approve(string taskId)
    {
        lock (_gate)
        {
            var entry = _tasks[taskId];
            if (entry.State != WorkTaskState.InReview)
                throw new InvalidOperationException("Somente entregas em revisão podem ser aprovadas.");
            entry.State = WorkTaskState.Completed;
        }
        Changed?.Invoke();
    }

    public void Retry(string taskId)
    {
        lock (_gate)
        {
            var entry = _tasks[taskId];
            if (entry.Cancellation is not null || entry.State is not
                (WorkTaskState.InReview or WorkTaskState.Failed or WorkTaskState.Cancelled))
                throw new InvalidOperationException("A tarefa ainda não pode voltar à fila.");
            entry.State = WorkTaskState.Queued;
        }
        Changed?.Invoke();
    }

    public void Cancel(string taskId)
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            var entry = _tasks[taskId];
            if (entry.State is not (WorkTaskState.Queued or WorkTaskState.Running))
                throw new InvalidOperationException("Somente tarefas na fila ou executando podem ser canceladas.");
            entry.State = WorkTaskState.Cancelled;
            cancellation = entry.Cancellation;
        }
        // Callbacks belong to the adapter and must not execute under the coordinator lock.
        try { cancellation?.Cancel(); }
        catch (ObjectDisposedException) { /* The attempt finished before cancellation reached it. */ }
        Changed?.Invoke();
    }

    public void CancelAll()
    {
        foreach (var task in Snapshot.Tasks.Where(t => t.State is WorkTaskState.Queued or WorkTaskState.Running))
        {
            try { Cancel(task.Definition.Id); }
            catch (InvalidOperationException) { /* Completion can win the race with shutdown. */ }
        }
    }

    private async Task ExecuteAsync(Reservation reservation)
    {
        var (entry, run, request, cancellation) = reservation;
        try
        {
            var result = await _adapters[request.Function.Provider].ExecuteAsync(request,
                new InlineProgress(e => AddEvent(entry, run.Id, e)), cancellation.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(result.Summary)) throw new InvalidOperationException("O provedor retornou uma entrega vazia.");
            lock (_gate)
            {
                var cancelled = entry.State == WorkTaskState.Cancelled;
                Finish(entry, cancelled ? RunState.Cancelled : RunState.Succeeded, cancelled ? null : result.Summary, null);
                entry.State = cancelled ? WorkTaskState.Cancelled : WorkTaskState.InReview;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            lock (_gate) { Finish(entry, RunState.Cancelled, null, null); entry.State = WorkTaskState.Cancelled; }
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                var cancelled = entry.State == WorkTaskState.Cancelled;
                Finish(entry, cancelled ? RunState.Cancelled : RunState.Failed, null, cancelled ? null : ex.Message);
                entry.State = cancelled ? WorkTaskState.Cancelled : WorkTaskState.Failed;
            }
        }
        finally
        {
            lock (_gate) entry.Cancellation = null;
            cancellation.Dispose();
            Changed?.Invoke();
        }
        // Refill a free slot immediately, even when another provider is still working.
        await DispatchAsync().ConfigureAwait(false);
    }

    private void AddEvent(Entry entry, string runId, ProviderEvent value)
    {
        lock (_gate)
        {
            var run = entry.Runs[^1];
            if (run.Id != runId || run.State != RunState.Running || entry.State != WorkTaskState.Running) return;
            var text = value.Text.Length > 4000 ? value.Text[..4000] : value.Text;
            entry.Runs[^1] = run with { Events = Array.AsReadOnly(run.Events.TakeLast(99).Append(value with { Text = text }).ToArray()) };
        }
        Changed?.Invoke();
    }

    private static void Finish(Entry entry, RunState state, string? result, string? error) =>
        entry.Runs[^1] = entry.Runs[^1] with { State = state, FinishedAt = DateTimeOffset.UtcNow, Result = result, Error = error };

    private CoordinatorSnapshot SnapshotUnsafe() => new(Array.AsReadOnly(_functions.Values.ToArray()),
        Array.AsReadOnly(_tasks.Values.Select(t => new TaskSnapshot(t.Definition,
            t.State == WorkTaskState.Queued || t.Runs.Count == 0 ? _functions[t.Definition.FunctionId].Provider : t.Runs[^1].Session.Provider,
            t.State, Array.AsReadOnly(t.Runs.ToArray()))).ToArray()));

    private sealed class Entry(WorkTask definition)
    {
        public WorkTask Definition { get; } = definition;
        public WorkTaskState State { get; set; }
        public List<TaskRun> Runs { get; } = [];
        public CancellationTokenSource? Cancellation { get; set; }
    }

    private sealed record Reservation(Entry Entry, TaskRun Run, ProviderRequest Request, CancellationTokenSource Cancellation);
    private sealed class InlineProgress(Action<ProviderEvent> report) : IProgress<ProviderEvent>
    {
        public void Report(ProviderEvent value) => report(value);
    }
}
