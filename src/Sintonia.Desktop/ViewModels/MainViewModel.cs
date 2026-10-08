using System.Collections.ObjectModel;
using System.Windows.Threading;
using Sintonia.Core;
using Sintonia.Infrastructure;

namespace Sintonia.Desktop.ViewModels;

public sealed record SessionViewModel(string Provider, string FunctionName, string TaskTitle, string Attempt, string State);

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly TaskCoordinator _coordinator;
    private TaskViewModel? _selectedTask;
    private string _notice = "Distribua as duas primeiras tarefas. Revise e aprove as entregas para liberar as próximas.";
    private bool _disposed;
    private CoordinatorSnapshot _snapshot;
    public ObservableCollection<FunctionViewModel> Functions { get; } = [];
    public ObservableCollection<TaskViewModel> Tasks { get; } = [];
    public ObservableCollection<SessionViewModel> Sessions { get; } = [];
    public AsyncCommand DispatchCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public DelegateCommand RetryCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public int QueuedCount => _snapshot.Tasks.Count(t => t.State == WorkTaskState.Queued);
    public int RunningCount => _snapshot.Tasks.Count(t => t.IsExecuting);
    public int ReviewCount => _snapshot.Tasks.Count(t => t.State == WorkTaskState.InReview);
    public int ApprovedCount => _snapshot.Tasks.Count(t => t.State == WorkTaskState.Completed);
    public string ProgressText => $"{ApprovedCount} de {Tasks.Count} entregas aprovadas";
    public bool HasSessions => Sessions.Count > 0;

    public TaskViewModel? SelectedTask
    {
        get => _selectedTask;
        set { Set(ref _selectedTask, value); RefreshCommands(); }
    }

    public MainViewModel(Dispatcher dispatcher, TaskCoordinator? coordinator = null)
    {
        _dispatcher = dispatcher;
        _coordinator = coordinator ?? DemoScenario.Create();
        _snapshot = _coordinator.Snapshot;
        DispatchCommand = new(DispatchAsync, ShowError, () => QueuedCount > 0);
        ApproveCommand = new(ApproveAsync, ShowError, () => SelectedTask?.CanApprove == true);
        RetryCommand = new(() => Safely(() => { _coordinator.Retry(SelectedTask!.Id); Notice = "Tarefa devolvida à fila. Selecione “Distribuir fila” para executar uma nova tentativa."; }), () => SelectedTask?.CanRetry == true);
        CancelCommand = new(() => Safely(() => { _coordinator.Cancel(SelectedTask!.Id); Notice = "Cancelamento solicitado para a tarefa selecionada."; }), () => SelectedTask?.CanCancel == true);
        foreach (var function in _snapshot.Functions)
            Functions.Add(new(function, (id, provider) => Safely(() => _coordinator.AssignProvider(id, provider))));
        foreach (var task in _snapshot.Tasks)
            Tasks.Add(new(task.Definition, _snapshot.Functions.Single(f => f.Id == task.Definition.FunctionId).Name));
        _coordinator.Changed += OnChanged;
        Refresh();
        SelectedTask = Tasks[0];
    }

    private async Task DispatchAsync()
    {
        Notice = "Execuções simuladas em andamento. Limite: 2 simultâneas, 1 por provedor.";
        await _coordinator.DispatchAsync();
        if (!_disposed) Notice = ReviewCount > 0 ? "Há entregas aguardando sua revisão. Aprovar libera as tarefas dependentes." : "Fila atualizada. Confira os estados das tarefas.";
    }

    private async Task ApproveAsync()
    {
        var task = SelectedTask!;
        _coordinator.Approve(task.Id);
        Notice = $"Entrega #{task.Id} aprovada. Distribuindo tarefas liberadas.";
        await _coordinator.DispatchAsync();
        if (!_disposed) Notice = ApprovedCount == Tasks.Count ? "Demonstração concluída. Todas as entregas simuladas foram aprovadas." : "Aprovação registrada. Confira a próxima entrega em revisão.";
    }

    private void OnChanged()
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) Refresh();
        else _dispatcher.BeginInvoke(() => { if (!_disposed) Refresh(); });
    }

    private void Refresh()
    {
        _snapshot = _coordinator.Snapshot;
        foreach (var task in Tasks) task.Update(_snapshot.Tasks.Single(t => t.Definition.Id == task.Id), _snapshot);
        foreach (var function in Functions)
            function.Update(_snapshot.Functions.Single(f => f.Id == function.Id), _snapshot.Tasks.Any(t => t.Definition.FunctionId == function.Id && t.IsExecuting));
        Sessions.Clear();
        foreach (var task in _snapshot.Tasks)
            foreach (var run in task.Runs.OrderByDescending(r => r.StartedAt))
                Sessions.Add(new(run.Session.Provider.ToString(), _snapshot.Functions.Single(f => f.Id == task.Definition.FunctionId).Name,
                    task.Definition.Title, $"{run.Session.Id[..8]} · tentativa {run.Attempt}", run.State switch
                    { RunState.Running => "Executando · simulado", RunState.Succeeded => "Entrega simulada", RunState.Failed => "Falha", _ => "Cancelada" }));
        Notify(nameof(QueuedCount)); Notify(nameof(RunningCount)); Notify(nameof(ReviewCount));
        Notify(nameof(ApprovedCount)); Notify(nameof(ProgressText)); Notify(nameof(HasSessions));
        RefreshCommands();
    }

    private void RefreshCommands()
    {
        DispatchCommand.Refresh(); ApproveCommand.Refresh(); RetryCommand.Refresh(); CancelCommand.Refresh();
    }

    private void Safely(Action action) { try { action(); } catch (Exception exception) { ShowError(exception); Refresh(); } }
    private void ShowError(Exception exception) => Notice = $"Não foi possível concluir a ação: {exception.Message}";
    public void Dispose()
    {
        _disposed = true;
        _coordinator.Changed -= OnChanged;
        _coordinator.CancelAll();
    }
}
