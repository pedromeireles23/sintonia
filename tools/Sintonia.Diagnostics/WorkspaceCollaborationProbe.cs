using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;
using Sintonia.Infrastructure.Validation;

internal static class WorkspaceCollaborationProbe
{
    // Explicit opt-in diagnostic only. Exactly one worker turn per provider, no retries or planner inference.
    public static async Task RunAsync(CancellationToken token)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Colaboração Git ação " + Guid.NewGuid()));
        var repository = Path.Combine(directory, "projeto"); Directory.CreateDirectory(repository);
        Console.WriteLine("Prova finita: até dois turnos reais (Codex/Claude), 90s por execução e 6min por comando; sem reenvio. Pasta: " + directory);
        await File.WriteAllTextAsync(Path.Combine(repository, "AGENTS.md"),
            "# Pasta exclusiva de prova do Sintonia\nExecute somente o pedido atual. Preserve os demais arquivos. Não use outros agentes, rede, MCPs, comandos Git ou configurações globais. Pare em caso de recusa; não tente contornar.\n", token);
        await GitAsync(repository, token, "init", "--template=", "--initial-branch=main");
        await GitAsync(repository, token, "add", "--", "AGENTS.md"); await GitAsync(repository, token, "commit", "-m", "test: initialize finite collaboration probe");
        var store = new SqliteWorkspaceStore(Path.Combine(directory, "workspace.db")); await store.InitializeAsync();
        var project = await store.AddProjectAsync(repository); var marker = "SINTONIA-" + Guid.NewGuid().ToString("N");
        var data = JsonSerializer.Serialize(new { marker, values = new[] { 11, 14 }, sum = 25 });
        var plan = new PlanProposal(1, "Prova finita com Git", "Produzir dados e conferir a entrega integrada entre as duas ferramentas.",
        [
            new("data", "Produzir dados verificáveis", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite,
                "Prova finita. Crie somente numbers.json na pasta atual usando a ferramenta de edição, com este JSON exato: " + data
                + ". Não use shell, comandos Git, rede, MCPs ou outros agentes. Não altere nenhum outro arquivo. Pare após criar e responda em uma frase curta com o marcador e a soma.",
                ["numbers.json"], [], ["JSON contém marcador aleatório, valores 11/14 e soma 25; nenhum outro arquivo alterado."]),
            new("verify", "Conferir entrega integrada", "Documentação", ProviderKind.Claude, null, ConversationAccess.WorkspaceWrite,
                "Prova finita. Use somente Read para ler numbers.json da pasta atual. Confira a soma dos valores. Use Write uma única vez para criar verification.md com exatamente duas linhas: "
                + "primeiro o valor literal do campo marker; depois SUM= seguido da soma calculada. Não acrescente título ou comentários. Não use shell, comandos Git, rede, MCPs ou outros agentes. "
                + "Não altere outros arquivos. Em caso de recusa, pare sem repetir. Responda em uma frase curta com o marcador e a soma.",
                ["verification.md"], ["data"], ["Relatório repete o marcador da entrega integrada e SUM=25; dados originais preservados."])
        ]);
        var chief = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano fornecido pelo diagnóstico", ProviderKind.Codex,
            null, null, PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(chief);
        var source = new ChatRun(Guid.NewGuid().ToString(), chief.Id, "Plano fornecido, sem chamada de planejamento", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(source); await store.FinishRunAsync(source with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow,
            Response = "DIAGNÓSTICO: plano fornecido, sem inferência.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```" }, chief, []);
        var proposal = await store.CreateProposalAsync(project.Id, source.Id);
        proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
        await store.SaveProjectExecutionSettingsAsync(new(project.Id, MaxAttempts: 1, MaxExecutionSeconds: 90, MaxReportedTokens: 100_000, TokenReservation: 20_000));
        await RunTasksAsync(directory, store, project, batch, marker, token);
    }

    public static async Task ResumeAsync(string existingDirectory, CancellationToken token)
    {
        var directory = Path.GetFullPath(existingDirectory); var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes"));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(directory).StartsWith("Colaboração Git ação ", StringComparison.Ordinal))
            throw new ProviderException("Retome somente uma pasta exclusiva desta prova em artifacts/provider-probes.");
        var store = new SqliteWorkspaceStore(Path.Combine(directory, "workspace.db")); await store.InitializeAsync();
        var project = (await store.GetProjectsAsync()).Single(); var batch = (await store.GetTaskBatchesAsync(project.Id)).Single();
        if (!SamePath(project.Directory, Path.Combine(directory, "projeto")) || batch.Definition.Title != "Prova finita com Git"
            || batch.Tasks.Count != 2 || batch.Tasks[0].Definition.Id != "data" || batch.Tasks[1].Definition.Id != "verify"
            || batch.Tasks.Any(t => t.State is WorkspaceTaskState.Running or WorkspaceTaskState.Failed or WorkspaceTaskState.Cancelled or WorkspaceTaskState.Interrupted))
            throw new ProviderException("Estado incompatível; não repetir chamadas de modelos.");
        var first = batch.Tasks[0];
        if (first.Worktree is null || (await store.GetRunsAsync(first.ConversationId)).Count != 1)
            throw new ProviderException("A retomada exige a primeira entrega concluída, sem inferência adicional para Codex.");
        using var data = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(first.Worktree.WorkingDirectory, "numbers.json"), token));
        var marker = data.RootElement.GetProperty("marker").GetString()!;
        Console.WriteLine("Retomada explícita de prova: runs concluídos são reutilizados; no máximo os dois turnos originais. Pasta: " + directory);
        await RunTasksAsync(directory, store, project, batch, marker, token);
    }

    private static async Task RunTasksAsync(string directory, SqliteWorkspaceStore store, WorkspaceProject project,
        WorkspaceTaskBatch batch, string marker, CancellationToken token)
    {
        var repository = project.Directory;
        var manager = new GitTaskWorktreeManager(Path.Combine(directory, "worktrees")); var worktrees = new TaskWorktreeService(store, manager);
        var deliveries = new TaskDeliveryService(store, new GitTaskDeliveryInspector(manager));
        var preparations = new TaskIntegrationPreparationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskIntegrationPreparer(manager, Path.Combine(directory, "integrations")));
        var validations = new TaskIntegrationValidationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskIntegrationValidationInspector(manager), new ValidationCommandRunner());
        var publications = new TaskPublicationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskPublisher(manager));
        var diffs = new TaskDiffService(store, new GitTaskDiffReader(manager));
        var launch = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado; nenhum turno iniciado.");
        var askSettings = JsonSerializer.Serialize(new { permissions = new { ask = new[] { "Write", "Edit", "Bash", "PowerShell" } } });
        var claude = new ClaudeConversationProvider(launch with
        { PrefixArguments = launch.PrefixArguments.Concat(new[] { "--settings", askSettings, "--max-turns", "4" }).ToArray() });
        var chat = new WorkspaceChatService(store, [new CodexConversationProvider(), claude], manager);
        var executable = Path.Combine(AppContext.BaseDirectory, "Sintonia.Diagnostics.exe");
        if (!File.Exists(executable)) throw new ProviderException("Executável de validação ausente; nenhum turno iniciado.");
        var runs = new List<ChatRun>(); var published = new List<TaskPublication>();
        var permissionsPath = Path.Combine(directory, "permission-approvals.json");
        var permissionApprovals = File.Exists(permissionsPath) ? JsonSerializer.Deserialize<int>(await File.ReadAllTextAsync(permissionsPath, token)) : 0;
        foreach (var task in batch.Tasks)
        {
            var current = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks.Single(t => t.Id == task.Id);
            if (current.Worktree is null)
            {
                var planned = await worktrees.PreviewAsync(project.Id, task.Id, token); await worktrees.PrepareAsync(project.Id, planned, token);
                current = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks.Single(t => t.Id == task.Id);
            }
            var preview = current.Worktree!; var checkout = preview.WorkingDirectory;
            await manager.ValidateAsync(preview, token);
            var existing = await store.GetRunsAsync(current.ConversationId);
            if (existing.Count > 1 || existing.Any(r => r.State != ChatRunState.Completed)) throw new ProviderException("Turno existente não pode ser repetido.");
            ChatRun run;
            if (existing.Count == 1)
            {
                run = existing[0]; Console.WriteLine("Reutilizando turno concluído " + task.Definition.Provider + "; nenhum reenvio.");
            }
            else
            {
                Console.WriteLine("Iniciando único turno " + task.Definition.Provider + " na worktree da tarefa " + task.Definition.Id + ".");
                run = await chat.SendTaskAsync(project.Id, task.Id, new ProbeProgress(), token, async (permission, _) =>
                {
                    var matches = permission.Provider == ProviderKind.Claude && permission.Kind == "Write"
                        && SamePath(permission.WorkingDirectory, checkout) && MatchesReport(permission.Description, checkout, marker);
                    var allowed = matches && Interlocked.CompareExchange(ref permissionApprovals, 1, 0) == 0;
                    // Codex workspace writes are sandboxed; escalation and commands are refused by this diagnostic host.
                    if (allowed)
                    {
                        await File.WriteAllTextAsync(permissionsPath, JsonSerializer.Serialize(permissionApprovals), CancellationToken.None);
                    }
                    Console.WriteLine("Autorização " + permission.Provider + "/" + permission.Kind + ": " + (allowed ? "permitida para relatório esperado" : "recusada"));
                    return allowed;
                });
            }
            runs.Add(run);
            await File.WriteAllTextAsync(Path.Combine(directory, "runs.json"), JsonSerializer.Serialize(runs, new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
            if (run.State != ChatRunState.Completed) throw new ProviderException("Turno não concluído; não será repetido. " + run.Error);
            var complete = task.Definition.Id == "verify"; VerifyFiles(checkout, marker, complete);
            if (current.Publication?.State == TaskPublicationState.Published)
            {
                await manager.VerifyRevisionAsync(repository, current.Publication.Commit!, token);
                published.Add(current.Publication); VerifyFiles(repository, marker, complete); continue;
            }
            var expected = complete ? "verification.md" : "numbers.json";
            var review = await diffs.ScanAsync(project.Id, task.Id, token);
            if (review.Snapshot.Files.Count != 1 || review.Snapshot.Files[0].Path != expected || review.Snapshot.HeadCommit != (current.Delivery?.Commit ?? preview.BaseCommit))
                throw new ProviderException("A tarefa alterou arquivos/commits fora do escopo; não integrar nem repetir.");
            var diff = await diffs.ReadAsync(project.Id, review, review.Snapshot.Files[0], TaskDiffView.SinceBase, token);
            if (diff.State != TaskDiffContentState.Text || !diff.Text.Contains(marker, StringComparison.Ordinal)) throw new ProviderException("Diff não confirmou a entrega esperada.");
            await File.WriteAllTextAsync(Path.Combine(directory, task.Definition.Id + ".diff"), diff.Text, token);
            if (current.State != WorkspaceTaskState.Approved)
                await store.ReviewTaskAsync(project.Id, task.Id, run.Id, true, "Critérios de arquivos e diff conferidos pelo diagnóstico exclusivo; não representa aprovação automática em projetos do usuário.");
            if (current.Delivery is null)
            {
                await GitAsync(checkout, token, "add", "--", expected); await GitAsync(checkout, token, "commit", "-m", "test: record verified " + task.Definition.Id + " delivery");
                await deliveries.RegisterAsync(project.Id, await diffs.ScanAsync(project.Id, task.Id, token), token);
            }
            var combination = await preparations.PrepareAsync(project.Id, task.Id, await preparations.PreviewAsync(project.Id, task.Id, token), token);
            if (combination.State != TaskIntegrationPreparationState.Combined) throw new ProviderException("Combinação não concluída.");
            var config = await store.GetProjectValidationAsync(project.Id);
            await store.SaveProjectValidationAsync(config with { Commands = [new("Conferir arquivos da colaboração", executable,
                ["verify-collaboration", complete ? "complete" : "data", marker], TimeoutSeconds: 20)] });
            var validation = await validations.ValidateAsync(project.Id, await validations.PreviewAsync(project.Id, combination.Reservation.Id, token), token);
            if (validation.State != ValidationState.Passed) throw new ProviderException("Validação dos arquivos falhou. " + validation.Error);
            var publication = await publications.PublishAsync(project.Id, await publications.PreviewAsync(project.Id, validation.Reservation.Id, token), token);
            if (publication.State != TaskPublicationState.Published) throw new ProviderException("Publicação local não concluída. " + publication.Error);
            published.Add(publication); VerifyFiles(repository, marker, complete);
            Console.WriteLine("Entrega " + task.Definition.Id + " revisada, validada por processo real e publicada localmente. Tokens: " + (run.TokenUsage?.Describe() ?? "medição indisponível"));
        }
        var reopened = new SqliteWorkspaceStore(Path.Combine(directory, "workspace.db")); await reopened.InitializeAsync();
        var saved = (await reopened.GetTaskBatchesAsync(project.Id)).Single();
        if (saved.Tasks.Any(t => t.State != WorkspaceTaskState.Approved || t.Attempts != 1 || t.Publication?.State != TaskPublicationState.Published)
            || permissionApprovals != 1) throw new ProviderException("Reabertura/autorizações não conservaram os critérios.");
        var budget = await reopened.GetProjectTokenBudgetAsync(project.Id);
        if (budget.ReservedTokens != 0 || budget.ActiveRuns != 0 || budget.ReportedTokens != runs.Sum(r => r.TokenUsage?.TotalTokens ?? 0))
            throw new ProviderException("Contagem/reservas não conferem após reabertura.");
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
        { testedAt = DateTimeOffset.Now, marker, plannerInference = false, workerTurns = runs.Count, permissionApprovals,
            runs = runs.Select(r => new { r.Id, r.State, r.TokenUsage }), publications = published.Select(p => new { p.State, p.Commit }), budget },
            new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine("PASS: dois turnos reais, escrita Codex/Claude em worktrees, contexto/revisão integrada da dependência, diffs/revisão/validação/publicação serial, tokens e reabertura. " + budget.Describe() + " Evidência: " + directory);
    }

    public static void VerifyFiles(string directory, string marker, bool complete)
    {
        using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "numbers.json"))); var root = data.RootElement;
        if (root.EnumerateObject().Count() != 3 || root.GetProperty("marker").GetString() != marker
            || !root.GetProperty("values").EnumerateArray().Select(n => n.GetInt32()).SequenceEqual(new[] { 11, 14 }) || root.GetProperty("sum").GetInt32() != 25)
            throw new ProviderException("JSON não cumpriu os critérios.");
        if (complete && File.ReadAllText(Path.Combine(directory, "verification.md")).Replace("\r\n", "\n").TrimEnd('\n') != marker + "\nSUM=25")
            throw new ProviderException("Relatório não cumpriu os critérios.");
    }
    private static bool MatchesReport(string description, string checkout, string marker)
    {
        try
        {
            var separator = description.IndexOf("\n\n", StringComparison.Ordinal); if (separator < 0) return false;
            using var preview = JsonDocument.Parse(description[(separator + 2)..]); var input = preview.RootElement;
            return input.TryGetProperty("file_path", out var file) && SamePath(Path.GetFullPath(file.GetString()!, checkout), Path.Combine(checkout, "verification.md"))
                && input.TryGetProperty("content", out var content) && content.GetString()?.Replace("\r\n", "\n").TrimEnd('\n') == marker + "\nSUM=25";
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }
    private static bool SamePath(string first, string second) => Path.GetFullPath(first).Equals(Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase);
    private static async Task GitAsync(string directory, CancellationToken token, params string[] arguments)
    {
        var flags = new[] { "--no-replace-objects", "-c", "user.name=Sintonia Probe", "-c", "user.email=sintonia@example.invalid",
            "-c", "commit.gpgSign=false", "-c", "core.fsmonitor=false", "-c", "core.hooksPath=NUL" };
        var result = await ProcessProbe.RunAsync(new("git.exe", [], "Git da prova"), flags.Concat(arguments).ToArray(), directory, TimeSpan.FromSeconds(15), token);
        if (result.ExitCode != 0 || result.TimedOut || result.Truncated) throw new ProviderException("Git da prova não concluiu: " + result.StandardError);
    }
    private sealed class ProbeProgress : IProgress<ConversationEvent>
    {
        public void Report(ConversationEvent value)
        {
            if (value.Kind == ConversationEventKind.TokenUsage) Console.WriteLine("Consumo recebido: " + value.TokenUsage?.Describe());
        }
    }
}
