using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class TaskViewModel(WorkTask definition, string functionName) : ObservableObject
{
    private TaskSnapshot _snapshot = new(definition, ProviderKind.Codex, WorkTaskState.Queued, []);
    private string _dependencyText = "";
    public string Id => definition.Id;
    public string Title => definition.Title;
    public string Description => definition.Description;
    public string FunctionName => functionName;
    public WorkTaskState State => _snapshot.State;
    public string Provider => _snapshot.Provider.ToString();
    public bool CanApprove => State == WorkTaskState.InReview;
    public bool CanRetry => !_snapshot.IsExecuting && State is WorkTaskState.InReview or WorkTaskState.Failed or WorkTaskState.Cancelled;
    public bool CanCancel => State is WorkTaskState.Queued or WorkTaskState.Running;
    public string DependencyText => _dependencyText;
    public string AttemptText => _snapshot.LatestRun is { } run ? $"Tentativa {run.Attempt} · sessão simulada {run.Session.Id[..8]}" : "Nenhuma sessão iniciada";
    public string Result => _snapshot.LatestRun?.Result ?? _snapshot.LatestRun?.Error ??
        (State == WorkTaskState.Running ? "A sessão simulada está preparando um exemplo de entrega…"
            : State == WorkTaskState.Cancelled ? "Tarefa cancelada. Você pode devolvê-la à fila quando a tentativa parar."
            : "A entrega aparecerá aqui após a execução simulada. Selecione “Distribuir fila” para começar.");
    public string Events => string.Join("\n\n", _snapshot.LatestRun?.Events.Select(e => e.Text) ?? []);
    public string StateText => State switch
    {
        WorkTaskState.Queued => _dependencyText.StartsWith("Aguarda", StringComparison.Ordinal) ? "Aguarda aprovação" : "Na fila",
        WorkTaskState.Running => "Executando · simulado",
        WorkTaskState.InReview => "Em revisão",
        WorkTaskState.Completed => "Aprovada",
        WorkTaskState.Failed => "Falha",
        _ => "Cancelada"
    };

    public void Update(TaskSnapshot snapshot, CoordinatorSnapshot all)
    {
        _snapshot = snapshot;
        var pending = definition.DependencyIds.Where(id => all.Tasks.Single(t => t.Definition.Id == id).State != WorkTaskState.Completed).ToArray();
        _dependencyText = definition.DependencyIds.Count == 0 ? "Sem dependências" : pending.Length == 0
            ? "Todas as dependências foram aprovadas" : $"Aguarda aprovação de: {string.Join(", ", pending.Select(id => $"#{id}"))}";
        Notify(null);
    }
}
