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

internal static class TaskDeliverySmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); var source = Path.Combine(root, "Portal de atendimento ação");
        var directory = Path.Combine(source, "portal"); Directory.CreateDirectory(directory);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); var manager = new GitTaskWorktreeManager(Path.Combine(root, "worktrees"));
        var inspector = new ControlledInspector(new GitTaskDeliveryInspector(manager)); var worker = new Worker();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [worker], manager), app.Dispatcher, () => directory,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager), new(store, new GitTaskDiffReader(manager)), new(store, inspector));
        var main = new WorkspaceWindow(vm); TaskQueueViewModel? queue = null; TaskQueueWindow? queueWindow = null; var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await GitAsync(source, 0, "init", "--template=", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(directory, "portal.txt"), "Nome do solicitante\n");
                await GitAsync(source, 0, "add", "."); await GitAsync(source, 0, "commit", "-m", "base");
                await vm.AddProjectAsync(directory); await Until(() => vm.CanConfigure); var project = vm.Project!;
                await SeedAsync(store, project, independentWrites: true); queue = await vm.LoadTaskQueueAsync(); queue.ConfirmWorktree = _ => true;
                queueWindow = new(queue) { Owner = main }; queueWindow.Show(); await queue.EnqueueAsync(); await Until(() => queue.CanPrepareWorktree);
                await queue.PrepareWorktreeAsync(); await Until(() => queue.CanReviewDiffs);
                ((TabControl)queueWindow.FindName("TaskTabs")).SelectedIndex = 3; queueWindow.UpdateLayout();
                Button(queueWindow, "OpenTaskDiffs").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var window = app.Windows.OfType<TaskDiffWindow>().Single(); var diff = window.ViewModel;
                await Until(() => !diff.Busy && diff.Review is not null);
                Require(!Button(window, "RegisterTaskDelivery").IsEnabled && diff.DeliveryStatus.Contains("aprove"), "Tarefa pendente permitiu registro.");
                var task = queue.SelectedTask!.Record;
                await CompleteAsync(store, project, task, manager, "Validação acessível\n"); await queue.InitializeAsync();
                await diff.RefreshAsync(); Require(!diff.CanRegister && diff.DeliveryStatus.Contains("Salve"), "Arquivos sem commit permitiram registro.");
                await GitAsync(task.Worktree!.CheckoutDirectory, 0, "add", "."); await GitAsync(task.Worktree.CheckoutDirectory, 0, "commit", "-m", "entrega revisada");
                await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt");
                var register = Button(window, "RegisterTaskDelivery"); Require(register.IsEnabled, "Botão de registro não está vinculado ao conteúdo aprovado.");
                diff.ConfirmDelivery = _ => false; register.Command.Execute(null); await Until(() => !diff.Busy && diff.Notice.Contains("não confirmado"));
                Require(diff.Delivery is null && inspector.Calls == 0, "Recusa conferiu/salvou a entrega.");
                diff.ConfirmDelivery = _ => { File.WriteAllText(Path.Combine(task.Worktree.WorkingDirectory, "posterior.txt"), "Mudança durante confirmação."); return true; };
                register.Command.Execute(null); await Until(() => !diff.Busy && diff.Review is null);
                Require(diff.Delivery is null && diff.ContentText == "", "Mudança após a revisão registrou entrega obsoleta.");
                File.Delete(Path.Combine(task.Worktree.WorkingDirectory, "posterior.txt")); await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt");
                diff.ConfirmDelivery = _ => true; inspector.Mode = CaptureMode.Late; inspector.Reset();
                register.Command.Execute(null); await inspector.Started.Task; diff.CancelCommand.Execute(null);
                Require(diff.Busy && !diff.CanRegister, "Cancelar liberou o registro antes da captura parar."); inspector.Release.SetResult();
                await Until(() => !diff.Busy); Require(diff.Delivery is null && diff.Review is null, "Captura tardia publicou um registro cancelado.");
                inspector.Mode = CaptureMode.Native; await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt");
                var index = Path.Combine(task.Worktree.CommonGitDirectory, "worktrees", Path.GetFileName(task.Worktree.CheckoutDirectory), "index");
                var indexBefore = SHA256.HashData(await File.ReadAllBytesAsync(index)); var modifiedAt = File.GetLastWriteTimeUtc(index);
                register.Command.Execute(null); await Until(() => !diff.Busy && diff.Delivery is not null);
                var delivery = diff.Delivery!;
                Require(!diff.CanRegister && diff.DeliveryStatus.Contains(delivery.Commit) && diff.Notice.Contains("aguardando integração"), "Registro não mostra conteúdo/limite.");
                var indexAfter = SHA256.HashData(await File.ReadAllBytesAsync(index));
                Require(indexBefore.SequenceEqual(indexAfter) && modifiedAt == File.GetLastWriteTimeUtc(index)
                    && await File.ReadAllTextAsync(Path.Combine(directory, "portal.txt")) == "Nome do solicitante\n", "Registro modificou índice ou original.");
                Capture(window, Path.Combine(output, "01-delivery-normal.png")); window.Width = window.MinWidth; window.Height = window.MinHeight;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(window, Path.Combine(output, "02-delivery-minimum.png"));
                Require(((TextBox)window.FindName("DiffContent")).ActualHeight > 150 && register.IsVisible, "Registro/conteúdo ficaram inacessíveis no mínimo.");
                await diff.RefreshAsync(); Require(diff.Delivery == delivery && !diff.CanRegister, "Atualização perdeu o registro.");
                await queue.InitializeAsync(); Require(queue.WorktreeDetails.Contains(delivery.Commit), "Fila não mostra o commit após recarregar.");
                window.Close(); await Until(() => !window.IsVisible);
                Button(queueWindow, "OpenTaskDiffs").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window = app.Windows.OfType<TaskDiffWindow>().Single(); diff = window.ViewModel; await Until(() => !diff.Busy && diff.Review is not null);
                Require(diff.Delivery == delivery && !diff.CanRegister, "Reabertura perdeu o registro.");
                var other = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(other); await vm.AddProjectAsync(other); await Until(() => vm.CanConfigure);
                await diff.RefreshAsync(); Require(diff.Project.Id == project.Id && diff.Delivery == delivery, "Navegação trocou a entrega registrada.");

                // A second independent fixture task tests central shutdown during registration.
                queue.SelectedTask = queue.Tasks[1]; await Until(() => queue.CanPrepareWorktree); await queue.PrepareWorktreeAsync();
                var second = queue.SelectedTask!.Record; await CompleteAsync(store, project, second, manager, "Relatório de revisão\n");
                await GitAsync(second.Worktree!.CheckoutDirectory, 0, "add", "."); await GitAsync(second.Worktree.CheckoutDirectory, 0, "commit", "-m", "segunda entrega");
                var current = (await store.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks).Single(t => t.Id == second.Id);
                var pending = new TaskDiffWindow(vm.CreateTaskDiffReview(project, current)) { Owner = main }; pending.Show();
                await Until(() => !pending.ViewModel.Busy && pending.ViewModel.Review is not null); await SelectAsync(pending, "portal/portal.txt");
                pending.ViewModel.ConfirmDelivery = _ => true; inspector.Mode = CaptureMode.Blocking; inspector.Reset();
                var operation = pending.ViewModel.RegisterAsync(); await inspector.Started.Task; main.Close(); await operation;
                await Until(() => !main.IsVisible && !pending.IsVisible);
                Require(inspector.Cancelled && pending.ViewModel.Delivery is null && worker.Calls == 0, "Fechamento deixou registro/modelo ativo.");
                var reopened = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); await reopened.InitializeAsync();
                var tasks = (await reopened.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks).ToArray();
                Require(tasks.Single(t => t.Id == task.Id).Delivery == delivery && tasks.Single(t => t.Id == second.Id).Delivery is null
                    && (await reopened.GetTaskIntegrationsAsync(project.Id)).Count == 0, "Banco perdeu registro ou declarou integração inexistente.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: registro WPF com Git real, aprovação/commit exigidos, recusa, revisão obsoleta, captura tardia/cancelamento, commit/índice/original preservados, reabertura, navegação e fechamento da central; zero erros de binding. Runs/temporização simulados; nenhum modelo chamado. Capturas: " + output);
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                inspector.Release.TrySetResult(); foreach (var window in app.Windows.OfType<TaskDiffWindow>().ToArray()) { await window.ViewModel.StopAsync(); window.Close(); }
                await vm.StopAsync(); if (queue is not null) await queue.StopAsync(); if (queueWindow?.IsVisible == true) queueWindow.Close();
                if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task CompleteAsync(SqliteWorkspaceStore store, WorkspaceProject project, WorkspaceTask task, GitTaskWorktreeManager manager, string content)
    {
        await manager.ValidateAsync(task.Worktree!, CancellationToken.None);
        var conversation = (await store.GetConversationsAsync(project.Id)).Single(c => c.Id == task.ConversationId);
        var run = new ChatRun(Guid.NewGuid().ToString(), task.ConversationId, "SIMULAÇÃO: tarefa de teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run, task.Id, task.Worktree); await File.AppendAllTextAsync(Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"), content);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "SIMULAÇÃO: entrega de teste", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        await store.ReviewTaskAsync(project.Id, task.Id, run.Id, true, "Aprovação da fixture");
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Worker : IConversationProvider
    {
        public int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Este teste não deve chamar provedores."); }
    }
    private enum CaptureMode { Native, Late, Blocking }
    private sealed class ControlledInspector(IGitTaskDeliveryInspector native) : IGitTaskDeliveryInspector
    {
        public CaptureMode Mode { get; set; }
        public int Calls { get; private set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Cancelled = false; }
        public async Task<TaskDeliveryCommit> CaptureAsync(TaskDiffSnapshot snapshot, CancellationToken token)
        {
            Calls++; var captured = await native.CaptureAsync(snapshot, token);
            if (Mode == CaptureMode.Native) return captured;
            Started.TrySetResult(); if (Mode == CaptureMode.Late) { await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); return captured; }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
        public Task<TaskIntegrationTarget> InspectTargetAsync(TaskDelivery delivery, CancellationToken token) => native.InspectTargetAsync(delivery, token);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
