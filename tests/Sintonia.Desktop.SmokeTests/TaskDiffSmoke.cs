using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
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

internal static class TaskDiffSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Portal de atendimento ação"); var directory = Path.Combine(source, "portal"); Directory.CreateDirectory(directory);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db"));
        var manager = new GitTaskWorktreeManager(Path.Combine(root, "worktrees"));
        var reader = new ControlledReader(new GitTaskDiffReader(manager)); var worker = new Worker();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [worker], manager), app.Dispatcher, () => directory,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager), new(store, reader));
        var main = new WorkspaceWindow(vm); TaskQueueWindow? queueWindow = null; TaskQueueViewModel? queue = null;
        var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready);
                await GitAsync(source, 0, "init", "--template=", "--initial-branch=main");
                await File.WriteAllTextAsync(Path.Combine(directory, "portal.txt"), "Nome do solicitante\n");
                await File.WriteAllTextAsync(Path.Combine(directory, "antigo.txt"), "Instruções de atendimento\n");
                await File.WriteAllTextAsync(Path.Combine(directory, "excluir.txt"), "Conteúdo obsoleto\n");
                await GitAsync(source, 0, "add", "-f", "."); await GitAsync(source, 0, "commit", "-m", "base");
                await vm.AddProjectAsync(directory); await Until(() => vm.CanConfigure);
                var project = vm.Project!; await SeedAsync(store, project);
                queue = await vm.LoadTaskQueueAsync(); queue.ConfirmWorktree = _ => true;
                queueWindow = new(queue) { Owner = main }; queueWindow.Show(); await queue.EnqueueAsync();
                await Until(() => queue.CanPrepareWorktree);
                ((TabControl)queueWindow.FindName("TaskTabs")).SelectedIndex = 3; queueWindow.UpdateLayout();
                Require(!Button(queueWindow, "OpenTaskDiffs").IsEnabled, "Tarefa sem worktree liberou a consulta.");
                await queue.PrepareWorktreeAsync(); await Until(() => queue.CanReviewDiffs);
                var task = queue.SelectedTask!.Record; var worktree = task.Worktree!;
                var tracked = Path.Combine(worktree.WorkingDirectory, "portal.txt");
                await File.AppendAllTextAsync(tracked, "Validação acessível\n");
                await GitAsync(worktree.CheckoutDirectory, 0, "add", "."); await GitAsync(worktree.CheckoutDirectory, 0, "commit", "-m", "validação");
                await File.AppendAllTextAsync(tracked, "Campos obrigatórios identificados\n");
                await GitAsync(worktree.CheckoutDirectory, 0, "mv", "portal/antigo.txt", "portal/instruções.txt");
                await GitAsync(worktree.CheckoutDirectory, 0, "rm", "portal/excluir.txt");
                await GitAsync(worktree.CheckoutDirectory, 0, "add", "portal/portal.txt");
                await File.AppendAllTextAsync(tracked, "Mensagem clara ao enviar\n");
                await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "manual.txt"), "Portal de atendimento\nInstruções para a equipe de suporte.\n");
                await File.WriteAllBytesAsync(Path.Combine(worktree.WorkingDirectory, "imagem.bin"), [0, 1, 2]);
                await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "grande.txt"), new string('x', 70000));
                await File.WriteAllTextAsync(Path.Combine(worktree.CheckoutDirectory, "resumo.txt"), "Documento fora da subpasta do projeto.\n");
                var index = Path.Combine(worktree.CommonGitDirectory, "worktrees", Path.GetFileName(worktree.CheckoutDirectory), "index");
                var beforeIndex = SHA256.HashData(await File.ReadAllBytesAsync(index)); var modifiedAt = File.GetLastWriteTimeUtc(index);
                var beforeOriginal = await File.ReadAllTextAsync(Path.Combine(directory, "portal.txt"));
                Button(queueWindow, "OpenTaskDiffs").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var window = app.Windows.OfType<TaskDiffWindow>().Single(); var diff = window.ViewModel;
                await Until(() => !diff.Busy && diff.Review is not null);
                Require(diff.Files.Count == 7 && diff.Summary.Contains("4 novos") && diff.Context.Contains(worktree.BaseCommit), "Lista/base incompleta.");
                Require(diff.Files.Single(f => f.Path.EndsWith("instruções.txt")).PreviousPath.Contains("antigo.txt")
                    && diff.Files.Single(f => f.Path.EndsWith("excluir.txt")).Description.Contains("Excluído"), "Renomeação/exclusão não aparecem.");
                await SelectAsync(window, "portal/portal.txt");
                Require(diff.ContentText.Contains("+Validação acessível") && diff.ContentText.Contains("+Mensagem clara"), "Comparação desde a base perdeu commits ou a pasta.");
                Capture(window, Path.Combine(output, "01-diffs-normal.png"));
                await CompareAsync(window, TaskDiffView.Index);
                Require(diff.ContentText.Contains("+Campos obrigatórios") && !diff.ContentText.Contains("+Mensagem clara"), "Índice incluiu mudança não preparada.");
                await CompareAsync(window, TaskDiffView.WorkingTree);
                Require(diff.ContentText.Contains("+Mensagem clara") && !diff.ContentText.Contains("+Campos obrigatórios"), "Pasta não comparou contra o índice.");
                await SelectAsync(window, "portal/manual.txt"); Require(diff.ContentText.Contains("equipe de suporte"), "UTF-8 novo não chegou à interface.");
                await CompareAsync(window, TaskDiffView.Index); Require(diff.Content?.State == TaskDiffContentState.NoChanges, "Arquivo não versionado apareceu no índice.");
                await CompareAsync(window, TaskDiffView.SinceBase);
                await SelectAsync(window, "portal/imagem.bin"); Require(diff.Content?.State == TaskDiffContentState.Binary && diff.ContentText == "", "Binário virou texto.");
                await SelectAsync(window, "portal/grande.txt"); Require(diff.Content?.State == TaskDiffContentState.TooLarge && diff.ContentText == "", "Arquivo grande mostrou prévia parcial.");
                await SelectAsync(window, "portal/portal.txt");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(window, Path.Combine(output, "02-diffs-minimum.png"));
                Require(((TextBox)window.FindName("DiffContent")).ActualHeight > 150 && Button(window, "RefreshTaskDiffs").IsVisible,
                    "Conteúdo ou atualização ficou inacessível no tamanho mínimo.");
                queue.SelectedTask = queue.Tasks[1]; await Until(() => queue.Session?.Record.Id == queue.SelectedTask.Record.ConversationId);
                Require(!queue.CanReviewDiffs, "Dependente sem pasta própria liberou consulta.");
                var other = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(other); await vm.AddProjectAsync(other); await Until(() => vm.CanConfigure);
                await diff.RefreshAsync(); Require(diff.Project.Id == project.Id && diff.TaskRecord.Id == task.Id && diff.Files.Count == 7,
                    "Navegação redirecionou a janela de diffs.");
                await SelectAsync(window, "portal/portal.txt");
                var beforeRefresh = diff.ContentText;
                await File.WriteAllTextAsync(Path.Combine(worktree.WorkingDirectory, "mudança.txt"), "Mudança posterior à lista.\n");
                await diff.ReadSelectedAsync(); Require(beforeRefresh.Length > 0 && diff.Review is null && diff.Files.Count == 0 && diff.ContentText == ""
                    && diff.Notice.Contains("Atualize"), "Mudança no status manteve conteúdo anterior como atual.");
                await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt");
                var afterIndex = SHA256.HashData(await File.ReadAllBytesAsync(index));
                Require(beforeIndex.SequenceEqual(afterIndex) && modifiedAt == File.GetLastWriteTimeUtc(index)
                    && beforeOriginal == await File.ReadAllTextAsync(Path.Combine(directory, "portal.txt")), "Consultas alteraram índice ou original.");

                // Build an actual merge conflict after the read-only preservation checks.
                await GitAsync(worktree.CheckoutDirectory, 0, "add", "."); await GitAsync(worktree.CheckoutDirectory, 0, "commit", "-m", "entrega");
                await GitAsync(source, 0, "checkout", "-b", "fixture-other", worktree.BaseCommit);
                await File.WriteAllTextAsync(Path.Combine(directory, "portal.txt"), "Nome alterado por outra entrega\n");
                await GitAsync(source, 0, "add", "."); await GitAsync(source, 0, "commit", "-m", "outra entrega");
                await File.WriteAllTextAsync(tracked, "Nome alterado nesta entrega\n");
                await GitAsync(worktree.CheckoutDirectory, 0, "add", "."); await GitAsync(worktree.CheckoutDirectory, 0, "commit", "-m", "alterar nome");
                await GitAsync(worktree.CheckoutDirectory, 1, "merge", "--no-edit", "fixture-other");
                await diff.RefreshAsync(); Require(diff.Summary.Contains("1 conflitos") && diff.Files.Single(f => f.Path == "portal/portal.txt").Description.Contains("Conflito"),
                    "Conflito real não foi destacado.");
                await SelectAsync(window, "portal/portal.txt"); Require(diff.ContentText.Contains("<<<<<<<"), "Marcadores de conflito foram ocultados.");
                Capture(window, Path.Combine(output, "03-diffs-conflict.png"));

                reader.Mode = ReadMode.Late; reader.Reset();
                var read = diff.ReadSelectedAsync(); await reader.Started.Task;
                Require(diff.Busy && diff.ContentText == "" && !diff.CanChoose, "Consulta pendente manteve prévia ou seleção disponível.");
                diff.CancelCommand.Execute(null); Require(diff.Busy, "Cancelamento liberou antes da resposta tardia encerrar.");
                reader.Release.SetResult(); await read;
                Require(diff.Review is null && diff.ContentText == "" && diff.Notice.Contains("cancelada"), "Resultado tardio foi publicado após cancelamento.");
                reader.Mode = ReadMode.Native; await diff.RefreshAsync(); await SelectAsync(window, "portal/portal.txt");
                reader.Mode = ReadMode.Blocking; reader.Reset(); read = diff.ReadSelectedAsync(); await reader.Started.Task;
                window.Close(); await read; await Until(() => !window.IsVisible);
                Require(reader.Cancelled && !diff.RefreshCommand.CanExecute(null), "Fechar diffs deixou consulta ativa.");
                reader.Mode = ReadMode.Native;
                window = new(vm.CreateTaskDiffReview(project, task)) { Owner = main }; window.Show(); diff = window.ViewModel;
                await Until(() => !diff.Busy && diff.Review is not null); await SelectAsync(window, "portal/portal.txt");
                reader.Mode = ReadMode.Blocking; reader.Reset(); read = diff.ReadSelectedAsync(); await reader.Started.Task;
                main.Close(); await read; await Until(() => !main.IsVisible && !window.IsVisible);
                Require(reader.Cancelled && !diff.Busy && worker.Calls == 0, "Fechamento da central deixou trabalho ativo ou chamou modelo.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: diffs WPF com Git real, botão da fila, base/índice/pasta, renomeação/exclusão, UTF-8/binário/limite, conflito real, índice/original preservados, navegação vinculada, estado alterado, cancelamento/resposta tardia e fechamento da janela/central; zero erros de binding. Temporização simulada; nenhum modelo chamado. Capturas: " + output);
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                reader.Release.TrySetResult();
                foreach (var window in app.Windows.OfType<TaskDiffWindow>().ToArray()) { await window.ViewModel.StopAsync(); window.Close(); }
                await vm.StopAsync(); if (queue is not null) await queue.StopAsync();
                if (queueWindow?.IsVisible == true) queueWindow.Close(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    internal static async Task SeedAsync(SqliteWorkspaceStore store, WorkspaceProject project, bool independentWrites = false)
    {
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano de teste", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly); await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Portal de atendimento", "Criar portal acessível", [
            new("portal", "Criar formulário acessível", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite, "Criar formulário", ["portal.txt"], [], ["Conferir acessibilidade"]),
            new("review", "Revisar formulário", "Revisão", ProviderKind.Claude, null, independentWrites ? ConversationAccess.WorkspaceWrite : ConversationAccess.ReadOnly,
                "Conferir critérios", ["portal.txt"], independentWrites ? [] : ["portal"], ["Relatório"])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Plano", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run); await store.FinishRunAsync(run with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow,
            Response = "SIMULAÇÃO: plano de teste.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```" }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id); await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
    }
    internal static async Task GitAsync(string directory, int exitCode, params string[] args)
    {
        var result = await ProcessProbe.RunAsync(new("git.exe", [], "Git teste"),
            new[] { "-c", "user.name=Sintonia Test", "-c", "user.email=sintonia@example.invalid", "-c", "commit.gpgSign=false", "-c", "core.hooksPath=NUL", "-c", "core.fsmonitor=false" }.Concat(args).ToArray(),
            directory, TimeSpan.FromSeconds(10)); Require(result.ExitCode == exitCode && !result.TimedOut && !result.Truncated, "Git recusou fixture: " + result.StandardError);
    }
    internal static async Task SelectAsync(TaskDiffWindow window, string path)
    {
        var vm = window.ViewModel; var list = (ListBox)window.FindName("DiffFiles");
        list.SelectedItem = vm.Files.Single(f => f.Path == path); list.ScrollIntoView(list.SelectedItem);
        await Until(() => !vm.Busy && vm.Content is not null);
        list.UpdateLayout(); list.ScrollIntoView(list.SelectedItem); window.UpdateLayout();
    }
    private static async Task CompareAsync(TaskDiffWindow window, TaskDiffView view)
    {
        var vm = window.ViewModel; ((ComboBox)window.FindName("DiffComparison")).SelectedItem = vm.Comparisons.Single(c => c.Value == view);
        await Until(() => !vm.Busy && vm.Content is not null);
    }
    internal static Button Button(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            try { return Button(VisualTreeHelper.GetChild(root, i), id); } catch (KeyNotFoundException) { }
        throw new KeyNotFoundException(id);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    internal static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado da revisão de diffs não chegou."); await Task.Delay(25); }
        await Task.Delay(20);
    }
    internal static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Worker : IConversationProvider
    {
        public int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Este teste não deve chamar provedores."); }
    }
    private enum ReadMode { Native, Late, Blocking }
    private sealed class ControlledReader(IGitTaskDiffReader native) : IGitTaskDiffReader
    {
        public ReadMode Mode { get; set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public void Reset() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Cancelled = false; }
        public Task<TaskDiffSnapshot> ScanAsync(TaskWorktree worktree, CancellationToken token) => native.ScanAsync(worktree, token);
        public async Task<TaskFileDiff> ReadAsync(TaskDiffSnapshot snapshot, TaskDiffFile file, TaskDiffView view, CancellationToken token)
        {
            if (Mode == ReadMode.Native) return await native.ReadAsync(snapshot, file, view, token);
            Started.TrySetResult();
            if (Mode == ReadMode.Late) { await Release.Task.WaitAsync(TimeSpan.FromSeconds(5)); return new(TaskDiffContentState.Text, "SIMULAÇÃO: conteúdo tardio", "SIMULAÇÃO", DateTimeOffset.UtcNow); }
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
