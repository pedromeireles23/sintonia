using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

internal static class SessionConcurrencySmoke
{
    public static int Run(string outputPath)
    {
        var root = Path.GetFullPath(Path.Combine(outputPath, "run-" + Guid.NewGuid().ToString("N")));
        var projectPath = Path.Combine(root, "Projeto geral ação"); Directory.CreateDirectory(projectPath);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db"));
        var manager = new GitTaskWorktreeManager(Path.Combine(root, "worktrees"));
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var codex = new Worker(ProviderKind.Codex); var claude = new Worker(ProviderKind.Claude);
        var vm = new WorkspaceViewModel(store, new(store, [codex, claude], manager), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager));
        var main = new WorkspaceWindow(vm); TaskQueueWindow? window = null; var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await TaskDiffSmoke.Until(() => vm.Ready);
                await TaskDiffSmoke.GitAsync(projectPath, 0, "init", "--template=", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(projectPath, "original.txt"), "Original preservado\n");
                await TaskDiffSmoke.GitAsync(projectPath, 0, "add", ".");
                await TaskDiffSmoke.GitAsync(projectPath, 0, "commit", "-m", "base");
                await vm.AddProjectAsync(projectPath); await TaskDiffSmoke.Until(() => vm.CanConfigure);
                var project = vm.Project!;
                Require(vm.SessionLimit == 3 && vm.SavedSessionLimit == 3, "Limite padrão não é 3.");
                vm.Function = vm.Functions.Single(f => f.Name == PlanProposalFormat.ChiefFunctionName);
                vm.Prompt = "PLANEJAR-TESTE"; vm.SendCommand.Execute(null);
                await TaskDiffSmoke.Until(() => vm.ActiveCount == 0 && vm.Notice.Contains("Proposta salva"));
                Require(codex.Requests.Single().Access == ConversationAccess.ReadOnly, "Chefe recebeu escrita.");
                var review = await vm.LoadProposalReviewAsync();
                var proposal = AssertSingle(await store.GetProposalsAsync(project.Id));
                Require(proposal.State == ProposalReviewState.Draft && review.Tasks.Count == 8, "Chefe não produziu plano revisável.");
                proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
                var queue = await vm.LoadTaskQueueAsync(); window = new(queue) { Owner = main }; window.Show();
                await queue.EnqueueAsync(); await TaskDiffSmoke.Until(() => queue.CanStartAvailable);
                Require(queue.Tasks.Count == 8 && codex.Requests.Count == 1 && claude.Requests.IsEmpty, "Encaminhar executou tarefas.");
                foreach (var task in queue.Tasks.Where(t => t.Record.Definition.Access == ConversationAccess.WorkspaceWrite).ToArray())
                {
                    var worktrees = new TaskWorktreeService(store, manager);
                    await worktrees.PrepareAsync(project.Id, await worktrees.PreviewAsync(project.Id, task.Record.Id, CancellationToken.None), CancellationToken.None);
                }
                await queue.InitializeAsync(); await TaskDiffSmoke.Until(() => queue.CanStartAvailable);
                var independent = queue.Tasks[0].Record;
                var standalone = vm.StartTaskAsync(project, independent);
                await TaskDiffSmoke.Until(() => vm.ActiveCount == 1 && codex.Requests.Count == 2);
                var firstGroup = queue.StartAvailableAsync();
                await TaskDiffSmoke.Until(() => codex.Requests.Count + claude.Requests.Count == 4 && vm.ActiveCount == 3);
                Require(queue.Tasks.Count(t => t.Running) == 3 && !queue.StartAvailableCommand.CanExecute(null), "Grupo não ocupa 3 vagas ou admite duplicação.");
                queue.CancelCommand.Execute(null);
                await TaskDiffSmoke.Until(() => codex.Cancellations + claude.Cancellations == 2);
                Require(queue.Busy && vm.ActiveCount == 3, "Cancelamento liberou vagas antes do executor parar.");
                Require(!standalone.IsCompleted, "Cancelar grupo encerrou uma tarefa iniciada fora dele.");
                vm.CancelTask(independent);
                await TaskDiffSmoke.Until(() => codex.Cancellations + claude.Cancellations == 3);
                codex.Release.TrySetResult(); claude.Release.TrySetResult(); await Task.WhenAll(firstGroup, standalone);
                Require(queue.Tasks.Count(t => t.Record.State == WorkspaceTaskState.Cancelled) == 3
                    && queue.Tasks.Count(t => t.Record.State == WorkspaceTaskState.Pending) == 5, "Grupo ultrapassou 3 ou iniciou dependente.");

                vm.SessionLimit = 7;
                Require(vm.SavedSessionLimit == 3 && vm.SaveSessionLimitCommand.CanExecute(null), "Rascunho alterou limite antes de aplicar.");
                var concurrentSettings = await store.GetProjectExecutionSettingsAsync(project.Id);
                await new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")).SaveProjectExecutionSettingsAsync(
                    concurrentSettings with { MaxConcurrentSessions = 5 });
                var rejected = false;
                try { await vm.SaveSessionLimitAsync(); }
                catch (InvalidOperationException) { rejected = true; }
                Require(rejected && vm.SavedSessionLimit == 5 && vm.SessionLimit == 7 && vm.SaveSessionLimitCommand.CanExecute(null),
                    "Edição obsoleta sobrescreveu o limite ou perdeu o rascunho ao atualizar a revisão.");
                await vm.SaveSessionLimitAsync(); Require(vm.SavedSessionLimit == 7, "Limite 7 não foi aplicado.");
                var expander = Descendants(main).OfType<Expander>().Single(); expander.IsExpanded = true;
                main.Width = main.MinWidth; main.Height = main.MinHeight;
                TaskDiffSmoke.Capture(main, Path.Combine(root, "01-settings-minimum.png")); expander.IsExpanded = false;
                codex.Reset(); claude.Reset();
                var secondGroup = queue.StartAvailableAsync();
                await TaskDiffSmoke.Until(() => vm.ActiveCount == 7 && codex.Requests.Count + claude.Requests.Count == 11);
                Require(queue.Tasks.Count(t => t.Running) == 7, "As sete sessões não apareceram em execução.");
                Require(codex.Requests.Count(r => r.Prompt != "PLANEJAR-TESTE") >= 4 && claude.Requests.Count >= 3,
                    "Provedores não ocuparam múltiplas vagas.");
                vm.NewConversationCommand.Execute(null); vm.Prompt = "oitava sessão";
                Require(!vm.CanSend, "Oitava sessão habilitada.");
                var otherPath = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(otherPath);
                await vm.AddProjectAsync(otherPath); await TaskDiffSmoke.Until(() => vm.CanConfigure);
                Require(vm.SavedSessionLimit == 3 && queue.Project.Id == project.Id && vm.ActiveCount == 7, "Navegação misturou projeto/limite/grupo.");
                Require(queue.SessionUsage.StartsWith("7/7 no projeto") && vm.SessionUsage.StartsWith("0/3 no projeto"),
                    "O contador da fila mudou para o projeto selecionado na central.");
                vm.Prompt = "sem vagas globais"; Require(!vm.CanSend, "Teto global foi ignorado.");
                vm.Project = project; await TaskDiffSmoke.Until(() => vm.Conversations.Count == 9 && vm.CanEditSessionLimit);
                vm.SessionLimit = 3; await vm.SaveSessionLimitAsync();
                Require(vm.ActiveCount == 7 && queue.Busy, "Reduzir limite cancelou sessões ativas.");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                TaskDiffSmoke.Capture(window, Path.Combine(root, "02-seven-sessions-minimum.png"));
                vm.SessionLimit = 7; await vm.SaveSessionLimitAsync();
                window.Width = 1200; window.Height = 1050;
                TaskDiffSmoke.Capture(window, Path.Combine(root, "03-seven-sessions-normal.png"));
                codex.Release.TrySetResult(); claude.Release.TrySetResult(); await secondGroup;
                Require(vm.ActiveCount == 0 && queue.Tasks.Count(t => t.Record.State == WorkspaceTaskState.AwaitingReview) == 7,
                    "Grupo aprovou entregas automaticamente ou perdeu resultados.");
                Require(queue.Tasks[^1].Record.State == WorkspaceTaskState.Pending, "Dependente iniciou sem aprovação/integração.");
                Require(await File.ReadAllTextAsync(Path.Combine(projectPath, "original.txt")) == "Original preservado\n"
                    && !File.Exists(Path.Combine(projectPath, "entrega-de-teste.txt")), "Escritores alteraram o original.");
                var checkouts = queue.Tasks.Where(t => t.Record.Worktree is not null).Select(t => t.Record.Worktree!).ToArray();
                Require(checkouts.Length == 3 && checkouts.Select(w => w.CheckoutDirectory).Distinct().Count() == 3
                    && checkouts.All(w => File.Exists(Path.Combine(w.WorkingDirectory, "entrega-de-teste.txt"))), "Entregas não estão em checkouts distintos.");
                var reopened = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); await reopened.InitializeAsync();
                Require((await reopened.GetProjectExecutionSettingsAsync(project.Id)).MaxConcurrentSessions == 7
                    && (await reopened.GetTaskBatchesAsync(project.Id)).Single().Tasks.Count == 8, "Reabertura perdeu limite ou sessões.");
                foreach (var task in queue.Tasks.Take(7))
                    await store.ReviewTaskAsync(project.Id, task.Record.Id, task.Record.LastRunId!, false, "Ajuste de teste para verificar fechamento.");
                await queue.InitializeAsync(); await TaskDiffSmoke.Until(() => queue.CanStartAvailable);
                codex.Reset(); claude.Reset(); var closingGroup = queue.StartAvailableAsync();
                await TaskDiffSmoke.Until(() => vm.ActiveCount == 7 && codex.Requests.Count + claude.Requests.Count == 18);
                window.Close(); await TaskDiffSmoke.Until(() => codex.Cancellations + claude.Cancellations == 7);
                Require(window.IsVisible && vm.ActiveCount == 7, "Fechamento não aguardou cancelamento dos sete executores.");
                codex.Release.TrySetResult(); claude.Release.TrySetResult(); await closingGroup;
                await TaskDiffSmoke.Until(() => !window.IsVisible && vm.ActiveCount == 0);
                Require((await store.GetTaskBatchesAsync(project.Id)).Single().Tasks.Take(7).All(t => t.State == WorkspaceTaskState.Cancelled), "Fechamento perdeu estados.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: chefia/plano/fila, limites 3–7, sete sessões Codex/Claude, três escritores em worktrees Git reais, cancelamento aguardado, navegação, revisão/dependências, reabertura e fechamento; zero erros de binding. Provedores simulados, nenhum modelo chamado. Capturas: " + root);
                exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                codex.Release.TrySetResult(); claude.Release.TrySetResult(); await vm.StopAsync();
                if (window?.IsVisible == true) { await window.ViewModel.StopAsync(); window.Close(); }
                if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }

    private static PlanProposal Plan() => new(1, "Projeto geral de teste", "Organizar análise, documentação e implementação.",
        Enumerable.Range(0, 8).Select(i => new ProposedTask("task-" + i, "SIMULAÇÃO: tarefa " + (i + 1),
            i is 0 or 1 or 3 ? "Desenvolvimento" : "Análise", i is 1 or 4 or 6 ? ProviderKind.Claude : ProviderKind.Codex,
            null, i is 0 or 1 or 3 ? ConversationAccess.WorkspaceWrite : ConversationAccess.ReadOnly,
            "Executar tarefa de teste " + i, ["."], i == 7 ? ["task-0"] : [], ["Resultado verificável."])).ToArray());

    private sealed class Worker(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public ConcurrentBag<ConversationRequest> Requests { get; } = [];
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _cancellations;
        public int Cancellations => Volatile.Read(ref _cancellations);
        public void Reset() { Release = new(TaskCreationOptions.RunContinuationsAsynchronously); _cancellations = 0; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão", id, "modelo-teste"));
            if (request.Prompt == "PLANEJAR-TESTE")
                return new(id, "modelo-teste", "SIMULAÇÃO: chefe.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(Plan()) + "\n```", ConversationOutcome.Completed, []);
            using var registration = token.Register(() => Interlocked.Increment(ref _cancellations));
            await Release.Task; token.ThrowIfCancellationRequested();
            if (request.Access == ConversationAccess.WorkspaceWrite)
                await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "entrega-de-teste.txt"), "Entrega simulada", token);
            return new(id, "modelo-teste", "SIMULAÇÃO: resultado verificável", ConversationOutcome.Completed, []);
        }
    }
    private static T AssertSingle<T>(IReadOnlyList<T> items) { Require(items.Count == 1, "Esperado um registro."); return items[0]; }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
