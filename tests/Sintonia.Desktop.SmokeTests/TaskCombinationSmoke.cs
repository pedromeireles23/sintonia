using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;
using static Sintonia.Desktop.SmokeTests.TaskDiffSmoke;

namespace Sintonia.Desktop.SmokeTests;

internal static class TaskCombinationSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); var original = Path.Combine(root, "Portal geral ação");
        var directory = Path.Combine(original, "portal"); Directory.CreateDirectory(directory);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); var manager = new GitTaskWorktreeManager(Path.Combine(root, "worktrees"));
        var deliveries = new TaskDeliveryService(store, new GitTaskDeliveryInspector(manager));
        var preparer = new ControlledPreparer(new GitTaskIntegrationPreparer(manager, Path.Combine(root, "combinações")));
        var preparations = new TaskIntegrationPreparationService(store, deliveries, new RepositoryIntegrationLock(), preparer); var worker = new Worker();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [worker], manager), app.Dispatcher, () => directory,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager), new(store, new GitTaskDiffReader(manager)), deliveries, preparations);
        var main = new WorkspaceWindow(vm); TaskDiffWindow? window = null; var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await GitAsync(original, 0, "init", "--template=", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(directory, "portal.txt"), "Formulário inicial\n");
                await File.WriteAllTextAsync(Path.Combine(original, ".gitignore"), "*.local\n");
                await GitAsync(original, 0, "add", "."); await GitAsync(original, 0, "commit", "-m", "base de teste");
                await vm.AddProjectAsync(directory); await Until(() => vm.CanConfigure); var project = vm.Project!; await SeedAsync(store, project);
                var proposal = (await store.GetProposalsAsync(project.Id)).Single(); var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
                var worktrees = new TaskWorktreeService(store, manager);
                await worktrees.PrepareAsync(project.Id, await worktrees.PreviewAsync(project.Id, batch.Tasks[0].Id, CancellationToken.None), CancellationToken.None);
                var task = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0];
                var conversation = (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == task.ConversationId);
                var run = new ChatRun(Guid.NewGuid().ToString(), task.ConversationId, "SIMULAÇÃO", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
                await store.BeginRunAsync(run, task.Id, task.Worktree);
                await File.WriteAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"), "Formulário acessível\n");
                await GitAsync(task.Worktree.CheckoutDirectory, 0, "add", "."); await GitAsync(task.Worktree.CheckoutDirectory, 0, "commit", "-m", "entrega simulada");
                await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "SIMULAÇÃO: conferido", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
                await store.ReviewTaskAsync(project.Id, task.Id, run.Id, true, "Conferido pela fixture");
                task = (await store.GetTaskBatchesAsync(project.Id)).Single().Tasks[0];
                window = new TaskDiffWindow(vm.CreateTaskDiffReview(project, task)) { Owner = main }; window.Show(); var diff = window.ViewModel;
                await Until(() => !diff.Busy && diff.Review is not null); Require(!diff.CanPrepareCombination, "Combinação habilitada sem registro.");
                await SelectAsync(window, "portal/portal.txt"); diff.ConfirmDelivery = _ => true; await diff.RegisterAsync();
                var prepare = Button(window, "PrepareTaskCombination"); Require(prepare.IsEnabled, "Botão não habilitado após registro.");
                diff.ConfirmCombination = _ => false; await diff.PrepareCombinationAsync();
                Require(preparer.Calls == 0 && (await preparations.GetAsync(project.Id)).Count == 0, "Recusa criou combinação.");
                diff.ConfirmCombination = _ => { File.WriteAllText(Path.Combine(directory, "posterior.txt"), "destino mudou"); return true; };
                await diff.PrepareCombinationAsync(); Require(diff.Review is null && preparer.Calls == 0, "Prévia obsoleta chegou ao executor.");
                File.Delete(Path.Combine(directory, "posterior.txt"));
                await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt"); diff.ConfirmCombination = _ => true;
                await File.WriteAllTextAsync(Path.Combine(directory, "config.local"), "arquivo original ignorado");
                var index = Path.Combine(task.Worktree!.CommonGitDirectory, "index"); var originalIndex = SHA256.HashData(await File.ReadAllBytesAsync(index));
                prepare.Command!.Execute(null); await Until(() => diff.Busy); Require(!prepare.IsEnabled, "Outra preparação permitida enquanto ocupada.");
                // Native Git may take longer than the generic UI polling bound; wait on the operation itself.
                await WaitIdleAsync(diff);
                var combined = diff.Preparation!; Require(combined.State == TaskIntegrationPreparationState.Combined && combined.Tree is not null, "Combinação real não registrada.");
                Require(diff.PreparationStatus.Contains(combined.CheckoutDirectory) && diff.Notice.Contains("pendentes"), "Resultado omite pasta/limite.");
                var afterIndex = SHA256.HashData(await File.ReadAllBytesAsync(index));
                var afterOriginal = await File.ReadAllTextAsync(Path.Combine(directory, "portal.txt"));
                var afterIgnored = await File.ReadAllTextAsync(Path.Combine(directory, "config.local"));
                Require(originalIndex.SequenceEqual(afterIndex) && afterOriginal == "Formulário inicial\n"
                    && afterIgnored == "arquivo original ignorado", "Combinação mudou original/índice/ignorado.");
                Capture(window, Path.Combine(output, "01-combined-normal.png")); window.Width = window.MinWidth; window.Height = window.MinHeight;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                ((ScrollViewer)window.FindName("DiffSummaryScroll")).ScrollToBottom(); window.UpdateLayout();
                Capture(window, Path.Combine(output, "02-combined-minimum.png"));
                Require(((TextBox)window.FindName("DiffContent")).ActualHeight > 140 && prepare.IsVisible, "Conteúdo/ação inacessível no mínimo.");
                await File.WriteAllTextAsync(Path.Combine(directory, "portal.txt"), "Outra entrega concorrente\n");
                await GitAsync(original, 0, "add", "."); await GitAsync(original, 0, "commit", "-m", "conflito de teste");
                await diff.PrepareCombinationAsync(); Require(diff.Preparation?.State == TaskIntegrationPreparationState.Conflicted && diff.PreparationStatus.Contains("portal/portal.txt"), "Conflito real não mostrado.");
                Capture(window, Path.Combine(output, "03-conflict-minimum.png"));
                var conflict = diff.Preparation; var other = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(other);
                await vm.AddProjectAsync(other); await Until(() => vm.CanConfigure); await diff.RefreshAsync();
                Require(diff.Project.Id == project.Id && diff.Preparation?.Reservation.Id == conflict!.Reservation.Id, "Navegação perdeu a combinação de origem.");
                window.Close(); await Until(() => !window.IsVisible); window = new TaskDiffWindow(vm.CreateTaskDiffReview(project, task)) { Owner = main }; window.Show(); diff = window.ViewModel;
                await Until(() => !diff.Busy && diff.Review is not null); Require(diff.Preparation?.State == TaskIntegrationPreparationState.Conflicted, "Reabertura perdeu conflito.");
                diff.ConfirmCombination = _ => true; preparer.Reset(late: true);
                var late = diff.PrepareCombinationAsync(); await preparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)); diff.CancelCommand.Execute(null);
                Require(diff.Busy, "Cancelamento soltou operação antes do executor parar."); preparer.Release.SetResult(); await late;
                Require(diff.Preparation is null && diff.Review is null, "Resposta tardia mostrou sucesso cancelado.");
                await diff.RefreshAsync(); Require(diff.Preparation?.State == TaskIntegrationPreparationState.NeedsAttention && diff.Preparation.Tree is null, "Atenção não recuperada.");
                diff.ConfirmCombination = _ => true; preparer.Reset(late: false);
                var closing = diff.PrepareCombinationAsync(); await preparer.Started.Task.WaitAsync(TimeSpan.FromSeconds(30)); main.Close(); await closing;
                await Until(() => !main.IsVisible && !window.IsVisible); Require(preparer.Cancelled && worker.Calls == 0, "Fechamento deixou executor/modelo ativo.");
                var reopened = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); await reopened.InitializeAsync();
                var saved = await reopened.GetTaskIntegrationPreparationsAsync(project.Id); Require(saved.Count == 4 && saved.Last().State == TaskIntegrationPreparationState.NeedsAttention, "Resultados/atenção não persistidos.");
                Require(await File.ReadAllTextAsync(Path.Combine(preparer.Intent!.CheckoutDirectory, "partial.txt")) == "efeito parcial preservado", "Fechamento apagou efeito parcial.");
                var finalBatch = (await reopened.GetTaskBatchesAsync(project.Id)).Single(); Require(!WorkspaceTaskPolicy.CanStart(finalBatch.Tasks[1], finalBatch.Tasks), "Combinação liberou dependente sem integração.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: combinação WPF com Git real, registro exigido, recusa, prévia obsoleta, combinação/conflito em pasta separada, original/índice/ignorado preservados, cancelamento/tardio, navegação/reabertura e fechamento; zero erros de binding. Runs/temporização simulados; nenhum modelo chamado. Capturas: " + output); exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                preparer.Release.TrySetResult(); if (window is not null) { await window.ViewModel.StopAsync(); if (window.IsVisible) window.Close(); }
                await vm.StopAsync(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task WaitIdleAsync(TaskDiffViewModel diff)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90)); while (diff.Busy) await Task.Delay(25, stop.Token); await Task.Delay(30); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class ControlledPreparer(IGitTaskIntegrationPreparer native) : IGitTaskIntegrationPreparer
    {
        public int Calls { get; private set; }
        private bool _controlled, _late;
        public bool Cancelled { get; private set; }
        public TaskIntegrationPreparation? Intent { get; private set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset(bool late) { _controlled = true; _late = late; Cancelled = false; Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public string GetCheckoutDirectory(TaskIntegrationReservation reservation) => native.GetCheckoutDirectory(reservation);
        public async Task<TaskIntegrationPreparation> PrepareAsync(TaskIntegrationPreparation intent, CancellationToken token)
        {
            Calls++; if (!_controlled) return await native.PrepareAsync(intent, token);
            Intent = intent; Directory.CreateDirectory(intent.CheckoutDirectory); await File.WriteAllTextAsync(Path.Combine(intent.CheckoutDirectory, "partial.txt"), "efeito parcial preservado"); Started.TrySetResult();
            if (_late) await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            else { try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { Cancelled = true; throw; } }
            return intent with { State = TaskIntegrationPreparationState.Combined, Tree = new('a', intent.Reservation.Delivery.Tree.Length) };
        }
    }
    private sealed class Worker : IConversationProvider
    {
        public int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Este teste não deve chamar provedores."); }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
