using System.Collections.ObjectModel;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record GitChangeRow(GitFileChange Change)
{
    public string Path => Change.Path;
    public string PreviousPath => Change.OriginalPath is { } original ? "Antes: " + original : "";
    public bool HasPreviousPath => Change.OriginalPath is not null;
    public string IndexText => Change.IsConflict ? "Conflito" : Change.IsUntracked ? "Não versionado" : Describe(Change.IndexStatus);
    public string WorktreeText => Change.IsConflict ? "Conflito" : Change.IsUntracked ? "Novo" : Describe(Change.WorktreeStatus);
    public string SubmoduleText => !IsSubmodule ? "" : !HasSubmoduleWorktreeChanges ? "Submódulo" : "Submódulo: " + string.Join(", ",
        new[] { Change.SubmoduleState[1] == 'C' ? "commit diferente" : null, Change.SubmoduleState[2] == 'M' ? "arquivos alterados" : null,
            Change.SubmoduleState[3] == 'U' ? "arquivos novos" : null }.Where(s => s is not null));
    public bool IsSubmodule => Change.SubmoduleState.StartsWith('S');
    public bool HasSubmoduleWorktreeChanges => IsSubmodule && Change.SubmoduleState.Skip(1).Any(c => c != '.');
    private static string Describe(char code) => code switch
    { '.' => "—", 'M' => "Modificado", 'A' => "Adicionado", 'D' => "Excluído", 'R' => "Renomeado", 'C' => "Copiado", 'T' => "Tipo alterado", _ => code.ToString() };
}

public sealed class GitDiagnosticsViewModel : ObservableObject
{
    private readonly IGitRepositoryInspector _inspector;
    private CancellationTokenSource? _refresh;
    private Task _operation = Task.CompletedTask;
    private GitRepositoryDiagnostic? _snapshot;
    private bool _busy, _stopping;
    private string _notice = "Atualize para consultar o estado local do projeto.";
    public GitDiagnosticsViewModel(WorkspaceProject project, IGitRepositoryInspector inspector)
    {
        Project = project; _inspector = inspector;
        RefreshCommand = new(RefreshAsync, exception => Notice = exception.Message, () => !Busy && !_stopping);
        CancelCommand = new(() => _refresh?.Cancel(), () => Busy && !_stopping);
    }
    public WorkspaceProject Project { get; }
    public ObservableCollection<GitChangeRow> Changes { get; } = [];
    public bool Busy { get => _busy; private set { Set(ref _busy, value); RefreshProperties(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public GitRepositoryDiagnostic? Snapshot => _snapshot;
    public string StatusTitle => Busy ? "Consultando Git…" : Snapshot?.State switch
    {
        GitDiagnosticState.Available when Snapshot.Changes.Any(c => c.IsConflict) => "Conflitos pendentes",
        GitDiagnosticState.Available when Snapshot.IsUnborn => "Repositório sem commits",
        GitDiagnosticState.Available when Snapshot.Changes.Count > 0 => "Alterações locais presentes",
        GitDiagnosticState.Available => "Sem alterações locais",
        GitDiagnosticState.NotRepository => "Projeto sem Git",
        GitDiagnosticState.GitUnavailable => "Git não instalado ou fora do PATH",
        GitDiagnosticState.BareRepository => "Repositório sem pasta de trabalho",
        GitDiagnosticState.Failed => "Diagnóstico não concluído",
        _ => "Sem diagnóstico atual"
    };
    public string RootDirectory => Snapshot?.RootDirectory ?? "—";
    public string Branch => Snapshot?.State != GitDiagnosticState.Available ? "—" : Snapshot.IsDetached ? "Sem branch (HEAD destacado)" : Snapshot.Branch ?? "—";
    public string HeadCommit => Snapshot?.State != GitDiagnosticState.Available ? "—" : Snapshot.IsUnborn ? "Sem commits" : Snapshot.HeadCommit ?? "—";
    public string Upstream => Snapshot?.State != GitDiagnosticState.Available ? "—" : Snapshot.Upstream is not { } upstream ? "Sem branch de acompanhamento"
        : Snapshot.Ahead is { } ahead && Snapshot.Behind is { } behind ? $"{upstream} · {ahead} à frente / {behind} atrás (referências locais)" : upstream;
    public string CheckedAt => Snapshot is null ? "—" : Snapshot.CheckedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
    public string ChangeSummary => Snapshot?.State != GitDiagnosticState.Available ? "Lista indisponível até concluir a consulta."
        : $"Caminhos: {Changes.Count} · Preparados: {Changes.Count(c => !c.Change.IsConflict && !c.Change.IsUntracked && c.Change.IndexStatus != '.')} · "
            + $"Na pasta: {Changes.Count(c => !c.Change.IsConflict && !c.Change.IsUntracked && (c.Change.WorktreeStatus != '.' || c.HasSubmoduleWorktreeChanges))} · "
            + $"Novos: {Changes.Count(c => c.Change.IsUntracked)} · Conflitos: {Changes.Count(c => c.Change.IsConflict)}";
    public AsyncCommand RefreshCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public Task RefreshAsync()
    {
        if (Busy || _stopping) return Task.CompletedTask;
        _refresh = new(); Busy = true; _snapshot = null; Changes.Clear();
        Notice = "Consultando a pasta selecionada e seu repositório. Nenhum modelo será chamado."; RefreshProperties();
        return _operation = RefreshCoreAsync(_refresh);
    }
    private async Task RefreshCoreAsync(CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            var result = await _inspector.InspectAsync(Project.Directory, stop.Token);
            if (_stopping || stop.IsCancellationRequested) { Notice = "Consulta cancelada. Atualize para obter um diagnóstico completo."; return; }
            _snapshot = result;
            foreach (var change in result.Changes) Changes.Add(new(change));
            Notice = result.Message;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { Notice = "Consulta cancelada. Atualize para obter um diagnóstico completo."; }
        catch (Exception)
        { Notice = "Não foi possível concluir o diagnóstico Git. Confira a pasta e atualize novamente."; }
        finally { _refresh = null; stop.Dispose(); Busy = false; }
    }
    public async Task StopAsync()
    { _stopping = true; _refresh?.Cancel(); RefreshProperties(); await _operation; }
    private void RefreshProperties()
    {
        foreach (var property in new[] { nameof(Snapshot), nameof(StatusTitle), nameof(RootDirectory), nameof(Branch), nameof(HeadCommit), nameof(Upstream), nameof(CheckedAt), nameof(ChangeSummary) }) Notify(property);
        RefreshCommand.Refresh(); CancelCommand.Refresh();
    }
}
