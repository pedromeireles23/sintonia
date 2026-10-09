using System.Collections.ObjectModel;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record TaskDiffOption(string Name, TaskDiffView Value);
public sealed record TaskDiffRow(TaskDiffFile File)
{
    public string Path => File.Path;
    public string Description => $"Base: {Describe(File.BaseStatus)} · Índice: {(File.LocalChange is { } local ? new GitChangeRow(local).IndexText : "—")} · Pasta: "
        + (File.HasUntrackedContent ? "Novo, não versionado" : File.LocalChange is { } change ? new GitChangeRow(change).WorktreeText : "—");
    public string PreviousPath => (File.OriginalPath is { } original ? "Na base: " + original : "")
        + (File.LocalChange?.OriginalPath is { } previous && previous != File.OriginalPath ? "\nNo índice: " + previous : "");
    private static string Describe(char code) => code switch
    { 'A' => "Adicionado", 'M' => "Modificado", 'D' => "Excluído", 'R' => "Renomeado", 'T' => "Tipo alterado", 'U' => "Conflito", _ => "—" };
}

public sealed class TaskDiffViewModel : ObservableObject
{
    private readonly TaskDiffService _service;
    private readonly TaskDeliveryService? _deliveries;
    private readonly TaskIntegrationPreparationService? _preparations;
    private readonly TaskIntegrationValidationService? _validations;
    private readonly TaskPublicationService? _publications;
    private TaskIntegrationPreparation? _preparation;
    private TaskDelivery? _delivery;
    private CancellationTokenSource? _stop;
    private Task _operation = Task.CompletedTask;
    private TaskDiffReview? _review;
    private TaskDiffRow? _file;
    private TaskDiffOption _comparison;
    private TaskFileDiff? _content;
    private bool _busy, _stopping;
    private string _notice = "Atualize para consultar a pasta registrada desta tarefa.";
    public TaskDiffViewModel(WorkspaceProject project, WorkspaceTask task, TaskDiffService service, TaskDeliveryService? deliveries = null,
        TaskIntegrationPreparationService? preparations = null, TaskIntegrationValidationService? validations = null, TaskPublicationService? publications = null)
    {
        Project = project; TaskRecord = task; _service = service; _deliveries = deliveries; _delivery = task.Delivery; _comparison = Comparisons[0];
        _preparations = preparations;
        _validations = validations;
        _publications = publications;
        RefreshCommand = new(RefreshAsync, ShowError, () => !Busy && !_stopping);
        ReadCommand = new(ReadSelectedAsync, ShowError, () => CanRead);
        CancelCommand = new(() => _stop?.Cancel(), () => Busy && !_stopping);
        RegisterCommand = new(RegisterAsync, ShowError, () => CanRegister);
        PrepareCombinationCommand = new(PrepareCombinationAsync, ShowError, () => CanPrepareCombination);
    }
    public WorkspaceProject Project { get; }
    public WorkspaceTask TaskRecord { get; }
    public ObservableCollection<TaskDiffRow> Files { get; } = [];
    public IReadOnlyList<TaskDiffOption> Comparisons { get; } = [new("Desde o commit de base", TaskDiffView.SinceBase),
        new("Preparado para commit", TaskDiffView.Index), new("Na pasta, sem preparar", TaskDiffView.WorkingTree)];
    public TaskDiffRow? SelectedFile
    {
        get => _file;
        set { if (Busy || !Set(ref _file, value)) return; _content = null; RefreshProperties(); _ = ReadSelectedAsync(); }
    }
    public TaskDiffOption Comparison
    {
        get => _comparison;
        set { if (Busy || !Set(ref _comparison, value)) return; _content = null; RefreshProperties(); _ = ReadSelectedAsync(); }
    }
    public TaskDiffReview? Review => _review;
    public TaskFileDiff? Content => _content;
    public TaskDelivery? Delivery => _delivery;
    public TaskIntegrationPreparation? Preparation => _preparation;
    public Func<TaskDiffReview, bool>? ConfirmDelivery { get; set; }
    public Func<TaskIntegrationTarget, bool>? ConfirmCombination { get; set; }
    public string PreparationStatus => Preparation is not { } preparation ? ""
        : $"Combinação registrada: {preparation.State switch { TaskIntegrationPreparationState.Combined => "combinada, aguardando validação", TaskIntegrationPreparationState.Conflicted => "com conflitos", TaskIntegrationPreparationState.Preparing => "preparação em andamento", _ => "precisa de atenção" }}\n"
            + $"Pasta separada: {preparation.CheckoutDirectory}\n"
            + $"Destino consultado: {preparation.Reservation.Target.Branch} · {preparation.Reservation.Target.Commit}\n"
            + (preparation.Tree is { } tree ? $"Árvore combinada: {tree}\n" : "")
            + (preparation.Conflicts is { Count: > 0 } conflicts ? "Conflitos: " + string.Join(" · ", conflicts) + "\n" : "")
            + (preparation.Error is { } error ? error + "\n" : "")
            + "Estado salvo da preparação. Consulte os resultados de validação e publicação no painel Validar combinação.";
    public string DeliveryStatus => Delivery is { } delivery ? $"Commit registrado da entrega: {delivery.Commit}\nRegistrado em {delivery.RegisteredAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}. "
        + (Review?.Task.Publication is { State: TaskPublicationState.Published } publication ? $"Integrada no commit {publication.Commit}." : "A integração ainda está pendente.")
        : (Review?.Task ?? TaskRecord).State != WorkspaceTaskState.Approved ? "Para registrar um commit, aprove a entrega na fila e atualize os diffs."
        : Files.Any(f => f.File.LocalChange is not null || f.File.HasUntrackedContent) ? "Salve as mudanças em um commit e atualize os diffs antes de registrar. Arquivos ignorados ficam fora da entrega."
        : "Confira a comparação desde a base e registre o commit revisado. O registro inclui toda a worktree e não integra a entrega.";
    public string Context => $"{Project.Name} · {TaskRecord.Definition.Title}\nBase registrada: {TaskRecord.Worktree?.BaseCommit}\nPasta da tarefa: {TaskRecord.Worktree?.WorkingDirectory}";
    public string Summary => Review is null ? "Lista indisponível até concluir a consulta."
        : $"{Files.Count} caminhos · {Files.Count(f => f.File.HasUntrackedContent)} novos · {Files.Count(f => f.File.LocalChange?.IsConflict == true)} conflitos\n"
            + $"HEAD: {Review.Snapshot.HeadCommit} · Consulta: {Review.Snapshot.CheckedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}";
    public string ContentText => Content?.Text ?? "";
    public string ContentStatus => Busy ? "Consultando…" : Content is null ? "Selecione um arquivo para ler a comparação."
        : Content.State switch { TaskDiffContentState.Binary => "Arquivo binário / prévia limitada", TaskDiffContentState.TooLarge => "Conteúdo excede o limite de prévia",
            TaskDiffContentState.NoChanges => "Sem diferença nesta comparação", _ => $"Lido em {Content.CheckedAt.ToLocalTime():HH:mm:ss}" };
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); RefreshProperties(); } }
    public bool CanChoose => !Busy && !_stopping;
    public bool CanRead => CanChoose && Review is not null && SelectedFile is not null;
    public bool CanRegister => CanChoose && _deliveries is not null && Delivery is null && Review?.Task.State == WorkspaceTaskState.Approved
        && Comparison.Value == TaskDiffView.SinceBase && !Files.Any(f => f.File.LocalChange is not null || f.File.HasUntrackedContent)
        && (Files.Count == 0 || Content is not null);
    public bool CanPrepareCombination => CanChoose && _preparations is not null && Delivery is not null
        && Review?.Task.State == WorkspaceTaskState.Approved && Review.Snapshot.HeadCommit == Delivery.Commit
        && !Files.Any(f => f.File.LocalChange is not null || f.File.HasUntrackedContent);
    public bool CanValidateCombination => CanChoose && _validations is not null && Preparation is not null;
    public TaskValidationViewModel CreateValidationReview() => CanValidateCombination
        ? new(Project, Preparation!, _validations!, _publications) : throw new InvalidOperationException("Atualize os diffs e prepare uma combinação antes de consultar sua validação.");
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public AsyncCommand RegisterCommand { get; }
    public AsyncCommand PrepareCombinationCommand { get; }
    public Task RefreshAsync()
    {
        if (!CanChoose) return Task.CompletedTask;
        ClearReview(); _stop = new(); Busy = true; Notice = "Consultando a worktree e o commit de base. Nenhum modelo será chamado.";
        return _operation = RefreshCoreAsync(_stop);
    }
    private async Task RefreshCoreAsync(CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            var review = await _service.ScanAsync(Project.Id, TaskRecord.Id, stop.Token);
            var preparations = _preparations is null ? null : await _preparations.GetAsync(Project.Id);
            stop.Token.ThrowIfCancellationRequested(); if (_stopping) return;
            _preparation = preparations?.LastOrDefault(p => p.Reservation.Delivery.TaskId == TaskRecord.Id);
            _review = review; _delivery = review.Task.Delivery; foreach (var file in review.Snapshot.Files) Files.Add(new(file));
            Notice = Files.Count == 0 ? "Nenhuma diferença desde a base ou alteração local nesta consulta." : "Lista consultada. Selecione um arquivo e a comparação desejada.";
        }
        catch (Exception exception) { ClearReview(); ShowError(exception); }
        finally { _stop = null; stop.Dispose(); Busy = false; }
    }
    public Task ReadSelectedAsync()
    {
        if (!CanRead) return Task.CompletedTask;
        var review = Review!; var file = SelectedFile!.File; var view = Comparison.Value;
        _content = null; _stop = new(); Busy = true; Notice = "Lendo a comparação selecionada; arquivos e índice serão preservados.";
        return _operation = ReadCoreAsync(review, file, view, _stop);
    }
    private async Task ReadCoreAsync(TaskDiffReview review, TaskDiffFile file, TaskDiffView view, CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            var result = await _service.ReadAsync(Project.Id, review, file, view, stop.Token);
            stop.Token.ThrowIfCancellationRequested(); if (_stopping) return;
            _content = result; Notice = result.Message;
        }
        catch (Exception exception) { ClearReview(); ShowError(exception); }
        finally { _stop = null; stop.Dispose(); Busy = false; }
    }
    private void ClearReview()
    { _review = null; _file = null; _content = null; Files.Clear(); Notify(nameof(SelectedFile)); RefreshProperties(); }
    private void ShowError(Exception exception) => Notice = exception is OperationCanceledException or TimeoutException
        ? "Consulta cancelada ou prazo excedido. Atualize para obter uma nova leitura." : exception.Message;
    public Task RegisterAsync()
    {
        if (!CanRegister) return Task.CompletedTask;
        var review = Review!; _stop = new(); Busy = true;
        return _operation = RegisterCoreAsync(review, _stop);
    }
    private async Task RegisterCoreAsync(TaskDiffReview review, CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            if (ConfirmDelivery?.Invoke(review) != true) { Notice = "Registro não confirmado. A entrega continua sem commit registrado."; return; }
            stop.Token.ThrowIfCancellationRequested(); Notice = "Conferindo o commit revisado e registrando a entrega. Nenhum merge ou modelo será executado.";
            _delivery = await _deliveries!.RegisterAsync(Project.Id, review, stop.Token);
            // A completed transaction remains registered, even if cancellation arrived while SQLite was committing.
            _review = review with { Task = review.Task with { Delivery = _delivery } };
            Notice = "Commit da entrega registrado. Arquivos preservados; dependentes continuam aguardando integração.";
        }
        catch (Exception exception) { ClearReview(); ShowError(exception); Notice += " Atualize para conferir se o registro foi concluído."; }
        finally { _stop = null; stop.Dispose(); Busy = false; }
    }
    public async Task StopAsync()
    { _stopping = true; _stop?.Cancel(); RefreshProperties(); await _operation; }
    private void RefreshProperties()
    {
        foreach (var property in new[] { nameof(Review), nameof(Content), nameof(Delivery), nameof(DeliveryStatus), nameof(Preparation), nameof(PreparationStatus), nameof(Summary), nameof(ContentText), nameof(ContentStatus), nameof(CanChoose), nameof(CanRead), nameof(CanRegister), nameof(CanPrepareCombination) }) Notify(property);
        RefreshCommand?.Refresh(); ReadCommand?.Refresh(); CancelCommand?.Refresh(); RegisterCommand?.Refresh(); PrepareCombinationCommand?.Refresh();
        Notify(nameof(CanValidateCombination));
    }

    public Task PrepareCombinationAsync()
    {
        if (!CanPrepareCombination) return Task.CompletedTask;
        _stop = new(); Busy = true; Notice = "Conferindo o commit registrado e o destino da combinação.";
        return _operation = PrepareCombinationCoreAsync(_stop);
    }
    private async Task PrepareCombinationCoreAsync(CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            var preview = await _preparations!.PreviewAsync(Project.Id, TaskRecord.Id, stop.Token);
            stop.Token.ThrowIfCancellationRequested(); if (_stopping) return;
            if (ConfirmCombination?.Invoke(preview) != true) { Notice = "Combinação não confirmada. Nenhuma pasta de integração foi criada."; return; }
            stop.Token.ThrowIfCancellationRequested(); _preparation = null; RefreshProperties();
            Notice = "Combinando em uma pasta separada. O projeto original será preservado; nenhum modelo será chamado.";
            _preparation = await _preparations.PrepareAsync(Project.Id, TaskRecord.Id, preview, stop.Token);
            Notice = _preparation.State == TaskIntegrationPreparationState.Conflicted
                ? "Combinação com conflitos. Confira os arquivos na pasta separada; o projeto original foi preservado."
                : "Arquivos combinados na pasta separada. Validação e publicação ainda pendentes; dependentes continuam bloqueados.";
        }
        catch (Exception exception)
        {
            ClearReview(); ShowError(exception); Notice += " Atualize para conferir a preparação salva e possíveis arquivos preservados.";
            // The service saves failure/partial effects before returning. Do not show an older successful preparation.
            _preparation = null;
        }
        finally { _stop = null; stop.Dispose(); Busy = false; }
    }
}
