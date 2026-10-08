namespace Sintonia.Core;

public sealed class ConcurrencyLimits
{
    public int Total { get; }
    public IReadOnlyDictionary<ProviderKind, int> PerProvider { get; }

    public ConcurrencyLimits(int total, int codex, int claude)
    {
        if (total < 1 || codex < 1 || claude < 1)
            throw new ArgumentOutOfRangeException(nameof(total), "Os limites devem ser positivos.");
        Total = total;
        PerProvider = new System.Collections.ObjectModel.ReadOnlyDictionary<ProviderKind, int>(
            new Dictionary<ProviderKind, int> { [ProviderKind.Codex] = codex, [ProviderKind.Claude] = claude });
    }
}

public static class SchedulingPolicy
{
    public static IReadOnlyList<string> SelectReady(IReadOnlyList<TaskSnapshot> tasks, ConcurrencyLimits limits)
    {
        var approved = tasks.Where(t => t.State == WorkTaskState.Completed)
            .Select(t => t.Definition.Id).ToHashSet(StringComparer.Ordinal);
        var running = tasks.Where(t => t.IsExecuting || t.State == WorkTaskState.Running).ToArray();
        var total = running.Length;
        var counts = Enum.GetValues<ProviderKind>().ToDictionary(p => p, p => running.Count(t => t.Provider == p));
        var admitted = new List<string>();
        foreach (var task in tasks)
        {
            if (total >= limits.Total) break;
            if (task.State != WorkTaskState.Queued || counts[task.Provider] >= limits.PerProvider[task.Provider]
                || !task.Definition.DependencyIds.All(approved.Contains)) continue;
            admitted.Add(task.Definition.Id);
            total++;
            counts[task.Provider]++;
        }
        return admitted.AsReadOnly();
    }

    public static void ValidateGraph(IReadOnlyList<WorkTask> tasks)
    {
        if (tasks.Any(t => string.IsNullOrWhiteSpace(t.Id)) || tasks.Select(t => t.Id).Distinct().Count() != tasks.Count)
            throw new ArgumentException("As tarefas precisam de identificadores únicos e não vazios.", nameof(tasks));
        var byId = tasks.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks) Visit(task.Id);

        void Visit(string id)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new ArgumentException("As dependências contêm um ciclo.", nameof(tasks));
            foreach (var dependency in byId[id].DependencyIds)
            {
                if (!byId.ContainsKey(dependency))
                    throw new ArgumentException($"Dependência desconhecida: {dependency}.", nameof(tasks));
                Visit(dependency);
            }
            visiting.Remove(id);
            visited.Add(id);
        }
    }
}
