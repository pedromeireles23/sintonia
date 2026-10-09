using System.Collections.ObjectModel;
using System.ComponentModel;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class QueueTaskItem(WorkspaceTask record) : ObservableObject
{
    private bool _running;
    public WorkspaceTask Record { get; } = record;
    public bool Running { get => _running; set { Set(ref _running, value); Notify(nameof(Description)); } }
    public string Title => Record.Definition.Title;
    public string Description => $"{Record.Definition.Id} · {Record.Definition.Provider} · {(Running ? "Executando" : TaskQueueViewModel.StateText(Record.State))}";
}
public sealed record TaskAttemptItem(int Number, ChatRun Run)
{
    public string Label => $"Tentativa {Number} · {RunStateText} · {Run.StartedAt.ToLocalTime():dd/MM HH:mm}";
    private string RunStateText => Run.State switch { ChatRunState.Running => "Executando", ChatRunState.Completed => "Concluída",
        ChatRunState.Blocked => "Bloqueada", ChatRunState.Failed => "Falha", ChatRunState.Cancelled => "Cancelada", _ => "Interrompida" };
}

public sealed class TaskQueueViewModel : ObservableObject, IDisposable
{
    private readonly IWorkspaceStore _store;
    private WorkspaceTaskBatch? _batch;
    private WorkspaceProposal? _proposal;
    private QueueTaskItem? _task;
    private TaskAttemptItem? _attempt;
    private ConversationViewModel? _session;
    private bool _busy, _loading;
    private CancellationTokenSource? _preparationStop;
    private CancellationTokenSource? _dispatchStop;
    private Task _preparationTask = Task.CompletedTask, _executionTask = Task.CompletedTask;
    private long _selectionRevision;
    private string _note = "", _notice = "Confirme um plano na revisão e encaminhe para esta fila.";
    public TaskQueueViewModel(IWorkspaceStore store, WorkspaceViewModel workspace, WorkspaceProject project)
    {
        _store = store; Workspace = workspace; Project = project;
        ReloadCommand = new(InitializeAsync, ShowError, () => !Busy);
        EnqueueCommand = new(EnqueueAsync, ShowError, () => !Busy && SelectedProposal is not null);
        StartCommand = new(StartAsync, ShowError, () => CanStart);
        StartAvailableCommand = new(StartAvailableAsync, ShowError, () => CanStartAvailable);
        CancelCommand = new(Cancel, () => Busy && (_preparationStop is not null || _dispatchStop is not null || Session?.Running == true));
        PrepareWorktreeCommand = new(PrepareWorktreeAsync, ShowError, () => CanPrepareWorktree);
        ApproveCommand = new(() => ReviewAsync(true), ShowError, () => CanReview);
        RequestChangesCommand = new(() => ReviewAsync(false), ShowError, () => CanReview && !string.IsNullOrWhiteSpace(ReviewNote));
        Workspace.PropertyChanged += WorkspaceChanged;
    }
    public WorkspaceViewModel Workspace { get; }
    public WorkspaceProject Project { get; }
    public ObservableCollection<WorkspaceTaskBatch> Batches { get; } = [];
    public ObservableCollection<WorkspaceProposal> ApprovedProposals { get; } = [];
    public ObservableCollection<QueueTaskItem> Tasks { get; } = [];
    public ObservableCollection<TaskAttemptItem> Attempts { get; } = [];
    public WorkspaceProposal? SelectedProposal { get => _proposal; set { Set(ref _proposal, value); Refresh(); } }
    public WorkspaceTaskBatch? SelectedBatch
    {
        get => _batch;
        set
        {
            if (Busy || !Set(ref _batch, value)) return;
            Tasks.Clear(); if (value is not null) foreach (var task in value.Tasks) Tasks.Add(new(task));
            SelectedTask = Tasks.FirstOrDefault(); Refresh();
        }
    }
    public QueueTaskItem? SelectedTask
    {
        get => _task;
        set
        {
            if (Busy || !Set(ref _task, value)) return;
            _ = LoadSelectionAsync(); Refresh();
        }
    }
    public TaskAttemptItem? SelectedAttempt { get => _attempt; set { Set(ref _attempt, value); Notify(nameof(HistoricalResponse)); Refresh(); } }
    public ConversationViewModel? Session
    {
        get => _session;
        private set
        {
            if (_session is not null) _session.PropertyChanged -= SessionChanged;
            Set(ref _session, value);
            if (_session is not null) _session.PropertyChanged += SessionChanged;
            Refresh();
        }
    }
    private void SessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConversationViewModel.Running)) return;
        if (SelectedTask is { } item) item.Running = Session?.Running == true;
        Refresh();
    }
    public string HistoricalResponse => SelectedAttempt?.Run.Response ?? "Ainda não há resultado nesta tentativa.";
    public string TaskDetails => SelectedTask is not { } item ? "Selecione uma tarefa." :
        $"{item.Record.Definition.FunctionName} · {item.Record.Definition.Model ?? "Modelo padrão"} · {(item.Record.Definition.Access == ConversationAccess.ReadOnly ? "Leitura" : "Alterações")}\n"
        + $"Escopo: {string.Join(", ", item.Record.Definition.Scope)}\nDependências: {string.Join(", ", item.Record.Definition.Dependencies.DefaultIfEmpty("Nenhuma"))}\n"
        + item.Record.Definition.Instructions + "\nCritérios:\n• " + string.Join("\n• ", item.Record.Definition.AcceptanceCriteria);
    public string TaskStatus => SelectedTask is not { } item ? "" : $"{(Session?.Running == true ? "Executando" : StateText(item.Record.State))} · {item.Record.Attempts + (Session?.Running == true && item.Record.State != WorkspaceTaskState.Running ? 1 : 0)}/{WorkspaceTaskPolicy.MaxAttempts} tentativas"
        + (item.Record.ReviewNote is { Length: > 0 } note ? "\nÚltima revisão: " + note : "")
        + (SelectedBatch is { } batch && item.Record.State == WorkspaceTaskState.Pending && item.Record.Definition.Dependencies.Any(id => batch.Tasks.Any(t => t.Definition.Id == id && t.State != WorkspaceTaskState.Approved))
            ? "\nAguarda aprovação das dependências." : "")
        + (SelectedBatch is { } current && item.Record.Definition.Dependencies.Any(id => current.Tasks.Any(t => t.Definition.Id == id && t.Worktree is not null))
            ? "\nAguarda integração Git das dependências. Esta etapa ainda está em desenvolvimento." : "");
    public string WorktreeDetails => SelectedTask?.Record.Worktree is not { } worktree
        ? $"Pasta usada nas tentativas: {Project.Directory}\n\nUma tarefa com escrita pode preparar outra pasta Git antes da primeira tentativa. A preparação é opcional e não chama modelos."
        : $"{WorktreeStateText(worktree.State)}\n\nBranch: {worktree.Branch}\nCommit de base: {worktree.BaseCommit}\n\nPasta usada nas tentativas:\n{worktree.WorkingDirectory}\n\nCheckout:\n{worktree.CheckoutDirectory}\n\nRepositório original:\n{worktree.RepositoryDirectory}"
            + (worktree.Error is { } error ? "\n\n" + error : "")
            + (SelectedTask.Record.Delivery is { } delivery ? $"\n\nCommit registrado da entrega:\n{delivery.Commit}\nIntegração pendente." : "");
    public string WorktreeExplanation => "A worktree parte de um commit salvo. Alterações locais, arquivos ignorados e dependências instaladas permanecem no original. Confira as instruções e configurações disponíveis na nova pasta antes de executar.\n\nTarefas independentes em worktrees distintas podem escrever em paralelo. Escrita direta no original é exclusiva. Aprovar uma entrega não integra seus arquivos; dependentes aguardam a integração Git, ainda em desenvolvimento.";
    public Func<TaskWorktree, bool>? ConfirmWorktree { get; set; }
    public bool CanPrepareWorktree => !Busy && !_loading && Workspace.CanPrepareWorktrees && SelectedTask is { } task && SelectedBatch is { } batch
        && WorkspaceTaskPolicy.CanPrepareWorktree(task.Record, batch.Tasks);
    public bool CanReviewDiffs => !Busy && !_loading && Workspace.CanReviewTaskDiffs && Session?.Running != true
        && SelectedTask?.Record is { State: not WorkspaceTaskState.Running, Worktree.State: TaskWorktreeState.Ready };
    public string ReviewNote { get => _note; set { Set(ref _note, value); Refresh(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Refresh(); } }
    public bool CanChoose => !Busy;
    public bool CanStart => !Busy && !_loading && SelectedTask is { } item && SelectedBatch is { } batch
        && WorkspaceTaskPolicy.CanStart(item.Record, batch.Tasks) && Workspace.CanStartTask(Project, item.Record);
    public bool CanStartAvailable => !Busy && !_loading && SelectedBatch is { } batch
        && Workspace.SelectAvailableTasks(Project, batch).Count > 0;
    public string SessionUsage => Workspace.SessionUsageFor(Project.Id);
    public bool CanReview => !Busy && !_loading && SelectedTask?.Record.State == WorkspaceTaskState.AwaitingReview
        && SelectedAttempt?.Run.Id == SelectedTask.Record.LastRunId;
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand EnqueueCommand { get; }
    public AsyncCommand StartCommand { get; }
    public AsyncCommand StartAvailableCommand { get; }
    public AsyncCommand PrepareWorktreeCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand RequestChangesCommand { get; }

    public async Task InitializeAsync()
    {
        if (Busy) return;
        Busy = true;
        try { await ReloadAsync(); await LoadSelectionAsync(); }
        finally { Busy = false; }
    }
    private async Task ReloadAsync()
    {
        var batches = await _store.GetTaskBatchesAsync(Project.Id); var proposals = await _store.GetProposalsAsync(Project.Id);
        var batchId = _batch?.Id; var taskId = _task?.Record.Id;
        Batches.Clear(); foreach (var batch in batches) Batches.Add(batch);
        ApprovedProposals.Clear();
        foreach (var proposal in proposals.Where(p => p.State == ProposalReviewState.Approved && !batches.Any(b => b.ProposalId == p.Id))) ApprovedProposals.Add(proposal);
        _proposal = ApprovedProposals.FirstOrDefault(); Notify(nameof(SelectedProposal));
        _batch = batches.FirstOrDefault(b => b.Id == batchId) ?? batches.FirstOrDefault(); Notify(nameof(SelectedBatch));
        Tasks.Clear(); if (_batch is not null) foreach (var task in _batch.Tasks) Tasks.Add(new(task));
        _task = Tasks.FirstOrDefault(t => t.Record.Id == taskId) ?? Tasks.FirstOrDefault(); Notify(nameof(SelectedTask));
        Refresh();
    }
    private async Task LoadSelectionAsync()
    {
        var revision = ++_selectionRevision; var task = SelectedTask?.Record; _loading = true;
        Session = null; Attempts.Clear(); SelectedAttempt = null; ReviewNote = ""; Refresh();
        try
        {
            if (task is null) return;
            var session = await Workspace.GetTaskConversationAsync(Project, task);
            var runs = await _store.GetRunsAsync(task.ConversationId);
            if (revision != _selectionRevision) return;
            Session = session;
            for (var i = 0; i < runs.Count; i++) Attempts.Add(new(i + 1, runs[i]));
            SelectedAttempt = Attempts.LastOrDefault();
        }
        catch (Exception exception) { if (revision == _selectionRevision) ShowError(exception); }
        finally { if (revision == _selectionRevision) { _loading = false; Refresh(); } }
    }
    public async Task EnqueueAsync()
    {
        if (Busy || SelectedProposal is not { } proposal) return;
        Busy = true;
        try
        {
            var batch = await _store.EnqueueProposalAsync(Project.Id, proposal.Id, proposal.Revision);
            _batch = batch; _task = null; await ReloadAsync();
            Notice = "Plano encaminhado. Selecione uma tarefa e inicie uma tentativa; nenhuma execução é automática.";
            await LoadSelectionAsync();
        }
        finally { Busy = false; }
    }
    public Task StartAsync() => CanStart ? _executionTask = StartCoreAsync() : Task.CompletedTask;
    private async Task StartCoreAsync()
    {
        if (!CanStart || SelectedTask is not { } item) return;
        Busy = true; Notice = "Executando. Autorizações aparecem neste painel; cancelar conserva possíveis efeitos no projeto.";
        try
        {
            var execution = Workspace.StartTaskAsync(Project, item.Record);
            // StartTaskAsync loads history before registering the live job.
            await Task.Yield(); Refresh();
            await execution;
            await ReloadAsync(); Notice = "Tentativa encerrada. Confira o resultado e os arquivos; dependências exigem aprovação e, quando houver worktree, integração Git.";
            await LoadSelectionAsync();
        }
        finally { Busy = false; }
    }

    public Task StartAvailableAsync() => CanStartAvailable ? _executionTask = StartAvailableCoreAsync() : Task.CompletedTask;
    private async Task StartAvailableCoreAsync()
    {
        if (!CanStartAvailable || SelectedBatch is not { } selected) return;
        Busy = true; _dispatchStop = new(); Refresh();
        try
        {
            var batch = (await _store.GetTaskBatchesAsync(Project.Id)).Single(b => b.Id == selected.Id);
            await Workspace.RefreshExecutionSettingsAsync(Project);
            _dispatchStop.Token.ThrowIfCancellationRequested();
            var ready = Workspace.SelectAvailableTasks(Project, batch);
            if (ready.Count == 0) { Notice = "Nenhuma tarefa disponível nas vagas e pastas atuais."; return; }
            Notice = $"Distribuindo {ready.Count} tarefas para suas sessões Codex/Claude. As demais aguardam novo início; não há repetição automática.";
            var executions = ready.Select(task => Workspace.StartTaskAsync(Project, task, _dispatchStop.Token)).ToArray();
            await Task.WhenAll(executions);
            Notice = "Grupo encerrado. Confira os resultados antes de aprovar; outras tarefas aguardam novo início.";
        }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _dispatchStop.Dispose(); _dispatchStop = null;
            try { await ReloadAsync(); await LoadSelectionAsync(); }
            finally { Busy = false; }
        }
    }

    private void Cancel()
    {
        if (_dispatchStop is not null) { _dispatchStop.Cancel(); return; }
        _preparationStop?.Cancel();
        if (Busy && SelectedTask is { } task) Workspace.CancelTask(task.Record);
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(WorkspaceViewModel.ActiveCount) or nameof(WorkspaceViewModel.SavedSessionLimit))) return;
        foreach (var task in Tasks) task.Running = Workspace.IsTaskRunning(task.Record);
        Refresh();
    }
    public async Task ReviewAsync(bool approve)
    {
        if (!CanReview || SelectedTask is not { } item || SelectedAttempt is not { } attempt) return;
        Busy = true;
        try
        {
            await _store.ReviewTaskAsync(Project.Id, item.Record.Id, attempt.Run.Id, approve, ReviewNote);
            await ReloadAsync(); Notice = approve ? item.Record.Worktree is null
                ? "Entrega aprovada. Dependências liberadas para início explícito."
                : "Entrega aprovada na worktree. Dependentes aguardam integração Git, ainda em desenvolvimento; arquivos preservados na pasta da tarefa."
                : "Ajustes registrados. Inicie outra tentativa quando estiver pronto; o pedido será enviado à mesma sessão.";
            await LoadSelectionAsync();
        }
        finally { Busy = false; }
    }
    public Task PrepareWorktreeAsync() => CanPrepareWorktree ? _preparationTask = PrepareWorktreeCoreAsync() : Task.CompletedTask;
    private async Task PrepareWorktreeCoreAsync()
    {
        if (!CanPrepareWorktree || SelectedTask is not { } item) return;
        Busy = true; _preparationStop = new(); Refresh();
        try
        {
            Notice = "Conferindo Git e o commit de base para a prévia. Nenhum modelo será chamado.";
            var preview = await Workspace.PreviewTaskWorktreeAsync(Project, item.Record, _preparationStop.Token);
            _preparationStop.Token.ThrowIfCancellationRequested();
            if (ConfirmWorktree?.Invoke(preview) != true) { Notice = "Preparação não confirmada. Nenhuma pasta foi criada."; return; }
            Notice = "Preparando a pasta Git. Aguarde o encerramento antes de iniciar uma tentativa.";
            await Workspace.PrepareTaskWorktreeAsync(Project, preview, _preparationStop.Token);
            Notice = "Worktree pronta. Confira a pasta e suas configurações; iniciar a tentativa é uma ação separada.";
        }
        catch (Exception exception) { ShowError(exception); }
        finally
        {
            _preparationStop.Dispose(); _preparationStop = null;
            try { await ReloadAsync(); await LoadSelectionAsync(); }
            finally { Busy = false; }
        }
    }
    private void ShowError(Exception exception) => Notice = exception is OperationCanceledException
        ? "Operação cancelada. Confira o estado e possíveis arquivos antes de retomar." : exception.Message;
    public async Task StopAsync()
    {
        Cancel();
        try { await Task.WhenAll(_preparationTask, _executionTask); }
        catch (Exception exception) { ShowError(exception); }
    }
    public void Dispose()
    {
        _selectionRevision++;
        Cancel(); Workspace.PropertyChanged -= WorkspaceChanged;
        if (_session is not null) _session.PropertyChanged -= SessionChanged;
    }
    private void Refresh()
    {
        Notify(nameof(CanChoose)); Notify(nameof(CanStart)); Notify(nameof(CanReview)); Notify(nameof(TaskDetails)); Notify(nameof(TaskStatus));
        Notify(nameof(CanStartAvailable)); Notify(nameof(SessionUsage));
        Notify(nameof(CanPrepareWorktree)); Notify(nameof(WorktreeDetails));
        Notify(nameof(CanReviewDiffs));
        ReloadCommand?.Refresh(); EnqueueCommand?.Refresh(); StartCommand?.Refresh(); CancelCommand?.Refresh(); ApproveCommand?.Refresh(); RequestChangesCommand?.Refresh();
        PrepareWorktreeCommand?.Refresh();
        StartAvailableCommand?.Refresh();
    }
    private static string WorktreeStateText(TaskWorktreeState state) => state switch
    { TaskWorktreeState.Preparing => "Preparação em andamento", TaskWorktreeState.Ready => "Worktree pronta", _ => "Preparação precisa de conferência" };
    public static string StateText(WorkspaceTaskState state) => state switch
    {
        WorkspaceTaskState.Pending => "Na fila", WorkspaceTaskState.Running => "Executando", WorkspaceTaskState.AwaitingReview => "Aguardando revisão",
        WorkspaceTaskState.Approved => "Entrega aprovada", WorkspaceTaskState.ChangesRequested => "Ajustes solicitados", WorkspaceTaskState.Blocked => "Permissão recusada",
        WorkspaceTaskState.Failed => "Falha", WorkspaceTaskState.Cancelled => "Cancelada", WorkspaceTaskState.Interrupted => "Interrompida", _ => "Estado desconhecido"
    };
}
