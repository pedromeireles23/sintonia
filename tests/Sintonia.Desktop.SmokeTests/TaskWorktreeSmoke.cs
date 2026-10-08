using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

internal static class TaskWorktreeSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Portal de atendimento ação"); var projectPath = Path.Combine(source, "portal"); Directory.CreateDirectory(projectPath);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db"));
        var native = new GitTaskWorktreeManager(Path.Combine(root, "worktrees")); var manager = new ControlledManager(native);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var worker = new Worker();
        var vm = new WorkspaceViewModel(store, new(store, [worker], native), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager));
        var main = new WorkspaceWindow(vm); TaskQueueWindow? window = null; TaskQueueViewModel? queue = null;
        var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready);
                await GitAsync(source, "init", "--template=", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(projectPath, "portal.txt"), "base");
                await File.WriteAllTextAsync(Path.Combine(source, ".gitignore"), "*.local\n");
                await GitAsync(source, "add", "-f", "."); await GitAsync(source, "commit", "-m", "base");
                await File.WriteAllTextAsync(Path.Combine(projectPath, "portal.txt"), "trabalho local preservado");
                await File.WriteAllTextAsync(Path.Combine(projectPath, "config.local"), "configuração ignorada");
                await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure);
                var project = vm.Project!; await SeedAsync(store, project);
                queue = await vm.LoadTaskQueueAsync(); TaskWorktree? preview = null;
                queue.ConfirmWorktree = plan => { preview = plan; return false; };
                window = new(queue) { Owner = main }; window.Show(); await queue.EnqueueAsync(); await Until(() => queue.CanPrepareWorktree);
                ((TabControl)window.FindName("TaskTabs")).SelectedIndex = 3;
                window.UpdateLayout(); await Task.Delay(20);
                var button = FindButton(window, "PrepareTaskWorktree"); Require(button.IsEnabled, "Preparação não está vinculada ao botão.");
                button.Command.Execute(null); await Until(() => !queue.Busy && preview is not null);
                Require(queue.SelectedTask!.Record.Worktree is null && !Directory.Exists(preview!.CheckoutDirectory), "Recusa criou checkout ou intenção.");
                queue.ConfirmWorktree = plan => { preview = plan; return true; };
                button.Command.Execute(null); await Until(() => !queue.Busy && queue.SelectedTask?.Record.Worktree?.State == TaskWorktreeState.Ready);
                Require(worker.Requests.Count == 0 && queue.CanStart && !queue.CanPrepareWorktree, "Preparação chamou modelo ou liberou outra criação.");
                var firstId = queue.SelectedTask!.Record.Id; var worktree = queue.SelectedTask.Record.Worktree!;
                Require(queue.WorktreeDetails.Contains(worktree.BaseCommit) && queue.WorktreeDetails.Contains(worktree.WorkingDirectory), "Diretório/base não aparecem.");
                Require(await File.ReadAllTextAsync(Path.Combine(projectPath, "portal.txt")) == "trabalho local preservado", "Original alterado durante preparação.");
                Require(!File.Exists(Path.Combine(worktree.WorkingDirectory, "config.local")), "Configuração ignorada foi copiada.");
                Capture(window, Path.Combine(output, "01-worktree-normal.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                Capture(window, Path.Combine(output, "02-worktree-minimum.png"));
                var execution = queue.StartAsync(); await Until(() => vm.HasPermission);
                Require(vm.Permission!.WorkingDirectory == worktree.WorkingDirectory, "Autorização mostra pasta errada.");
                vm.AllowPermissionCommand.Execute(null); await execution;
                queue.ReviewNote = "Acrescentar instruções acessíveis."; await queue.ReviewAsync(false);
                execution = queue.StartAsync(); await Until(() => vm.HasPermission); vm.AllowPermissionCommand.Execute(null); await execution;
                Require(worker.Requests.Count == 2 && worker.Requests[1].WorkingDirectory == worktree.WorkingDirectory
                    && worker.Requests[1].NativeSessionId == "worktree-smoke-session", "Retomada perdeu pasta ou sessão.");
                await queue.ReviewAsync(true); Require(queue.Notice.Contains("aguardam integração"), "Aprovação anuncia dependências liberadas sem integração.");
                queue.SelectedTask = queue.Tasks[1]; await Until(() => queue.Session?.Record.Id == queue.SelectedTask.Record.ConversationId);
                Require(!queue.CanStart && queue.TaskStatus.Contains("integração Git"), "Dependente iniciou sem integração.");
                var reopened = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); await reopened.InitializeAsync();
                Require((await reopened.GetTaskBatchesAsync(project.Id)).SelectMany(b => b.Tasks).Single(t => t.Id == firstId).Worktree == worktree, "Reabertura perdeu vínculo.");
                var otherPath = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(otherPath); await vm.AddProjectAsync(otherPath);
                Require(queue.Project.Id == project.Id && queue.WorktreeDetails.Contains(project.Directory), "A fila trocou de projeto ao navegar.");
                queue.SelectedTask = queue.Tasks[2]; await Until(() => queue.CanPrepareWorktree);
                manager.Wait = true; var preparation = queue.PrepareWorktreeAsync(); await Until(() => manager.Started && queue.CancelCommand.CanExecute(null));
                window.Close(); Require(window.IsVisible, "Janela fechou durante preparação ativa.");
                queue.CancelCommand.Execute(null); Require(queue.Busy, "Cancelamento liberou antes de salvar o estado."); await preparation;
                Require(queue.SelectedTask!.Record.Worktree?.State == TaskWorktreeState.NeedsAttention && queue.SelectedTask.Record.Attempts == 0,
                    "Cancelamento perdeu efeitos ou consumiu tentativa.");
                Require(queue.CanPrepareWorktree && !queue.CanStart, "Preparação parcial liberou modelo.");
                manager.Started = false; preparation = queue.PrepareWorktreeAsync(); await Until(() => manager.Started);
                main.Close(); await preparation; await Until(() => !main.IsVisible);
                Require(!window.IsVisible && !queue.Busy && queue.SelectedTask!.Record.Worktree?.State == TaskWorktreeState.NeedsAttention,
                    "Fechamento da central deixou preparação ativa.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: Git nativo, confirmação/recusa, subpasta/base, original preservado, tentativa e ajuste na mesma worktree/sessão, dependência sem integração bloqueada, reabertura, cancelamento e fechamento; zero erros de binding. Provedor e interrupções simulados; nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                await vm.StopAsync(); if (queue is not null) await queue.StopAsync();
                if (window?.IsVisible == true) window.Close(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task SeedAsync(SqliteWorkspaceStore store, WorkspaceProject project)
    {
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano de teste", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly); await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Portal de atendimento", "Criar portal acessível", [
            new("portal", "Criar formulário", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite, "Criar formulário", ["portal.txt"], [], ["Conferir acessibilidade"]),
            new("review", "Revisar formulário", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly, "Conferir critérios", ["portal.txt"], ["portal"], ["Relatório"]),
            new("docs", "Preparar documentação", "Documentação", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite, "Preparar instruções", ["docs/"], [], ["Instruções claras"])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Plano", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run); await store.FinishRunAsync(run with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow,
            Response = "SIMULAÇÃO: plano de teste.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```" }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id); await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
    }
    private static async Task GitAsync(string directory, params string[] args)
    {
        var result = await ProcessProbe.RunAsync(new("git.exe", [], "Git teste"),
            new[] { "-c", "user.name=Sintonia Test", "-c", "user.email=sintonia@example.invalid", "-c", "commit.gpgSign=false", "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false" }.Concat(args).ToArray(),
            directory, TimeSpan.FromSeconds(10)); Require(result.ExitCode == 0, "Git recusou fixture.");
    }
    private sealed class Worker : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public List<ConversationRequest> Requests { get; } = [];
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão de teste", "worktree-smoke-session", "modelo-teste"));
            var permission = new ConversationPermission(Kind, "Write", "SIMULAÇÃO: alterar portal.txt somente na pasta de teste.", request.WorkingDirectory);
            Require(request.PermissionHandler is not null && await request.PermissionHandler(permission, token), "Permissão de teste recusada.");
            await File.WriteAllTextAsync(Path.Combine(request.WorkingDirectory, "portal.txt"), "entrega simulada", token);
            return new("worktree-smoke-session", "modelo-teste", "SIMULAÇÃO: formulário para revisão; arquivo alterado somente na worktree de teste.", ConversationOutcome.Completed, []);
        }
    }
    private sealed class ControlledManager(IGitTaskWorktreeManager native) : IGitTaskWorktreeManager
    {
        public bool Wait { get; set; }
        public bool Started { get; set; }
        public Task<TaskWorktree> PlanAsync(WorkspaceProject project, string taskId, CancellationToken token) => native.PlanAsync(project, taskId, token);
        public async Task PrepareAsync(TaskWorktree worktree, CancellationToken token)
        {
            if (!Wait) { await native.PrepareAsync(worktree, token); return; }
            Directory.CreateDirectory(worktree.CheckoutDirectory); await File.WriteAllTextAsync(Path.Combine(worktree.CheckoutDirectory, "partial.txt"), "efeito de teste", token);
            Started = true; await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }
        public Task ValidateAsync(TaskWorktree worktree, CancellationToken token) => native.ValidateAsync(worktree, token);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    { var deadline = DateTime.UtcNow.AddSeconds(15); while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado da worktree não chegou."); await Task.Delay(20); } await Task.Delay(20); }
    private static Button FindButton(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { try { return FindButton(VisualTreeHelper.GetChild(root, i), id); } catch (KeyNotFoundException) { } }
        throw new KeyNotFoundException(id);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
