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
    private CancellationTokenSource? _stop;
    private Task _operation = Task.CompletedTask;
    private TaskDiffReview? _review;
    private TaskDiffRow? _file;
    private TaskDiffOption _comparison;
    private TaskFileDiff? _content;
    private bool _busy, _stopping;
    private string _notice = "Atualize para consultar a pasta registrada desta tarefa.";
    public TaskDiffViewModel(WorkspaceProject project, WorkspaceTask task, TaskDiffService service)
    {
        Project = project; TaskRecord = task; _service = service; _comparison = Comparisons[0];
        RefreshCommand = new(RefreshAsync, ShowError, () => !Busy && !_stopping);
        ReadCommand = new(ReadSelectedAsync, ShowError, () => CanRead);
        CancelCommand = new(() => _stop?.Cancel(), () => Busy && !_stopping);
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
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ReadCommand { get; }
    public DelegateCommand CancelCommand { get; }
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
            stop.Token.ThrowIfCancellationRequested(); if (_stopping) return;
            _review = review; foreach (var file in review.Snapshot.Files) Files.Add(new(file));
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
    public async Task StopAsync()
    { _stopping = true; _stop?.Cancel(); RefreshProperties(); await _operation; }
    private void RefreshProperties()
    {
        foreach (var property in new[] { nameof(Review), nameof(Content), nameof(Summary), nameof(ContentText), nameof(ContentStatus), nameof(CanChoose), nameof(CanRead) }) Notify(property);
        RefreshCommand?.Refresh(); ReadCommand?.Refresh(); CancelCommand?.Refresh();
    }
}
