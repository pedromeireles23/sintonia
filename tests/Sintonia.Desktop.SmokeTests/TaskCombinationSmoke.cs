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
using Sintonia.Infrastructure.Validation;
using System.Text.Json;
using static Sintonia.Desktop.SmokeTests.TaskDiffSmoke;

namespace Sintonia.Desktop.SmokeTests;

internal static class TaskCombinationSmoke
{
    public static int Run(string outputPath, bool validate = false, bool publish = false, bool cleanup = false)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); var original = Path.Combine(root, "Portal geral ação");
        var directory = Path.Combine(original, "portal"); Directory.CreateDirectory(directory);
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); var manager = new GitTaskWorktreeManager(Path.Combine(root, "worktrees"));
        var deliveries = new TaskDeliveryService(store, new GitTaskDeliveryInspector(manager));
        var preparer = new ControlledPreparer(new GitTaskIntegrationPreparer(manager, Path.Combine(root, "combinações")));
        var preparations = new TaskIntegrationPreparationService(store, deliveries, new RepositoryIntegrationLock(), preparer); var worker = new Worker();
        var runner = new ControlledRunner(new ValidationCommandRunner());
        var validations = new TaskIntegrationValidationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskIntegrationValidationInspector(manager), runner);
        var publications = new TaskPublicationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskPublisher(manager));
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [worker], manager), app.Dispatcher, () => directory,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), new(store, manager), new(store, new GitTaskDiffReader(manager)), deliveries, preparations, validations, publications,
            new TaskWorktreeCleanupService(store, new RepositoryIntegrationLock(), manager));
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
                if (validate)
                {
                    await ValidateUiAsync(output, main, window, store, runner, project, originalIndex, index, publish, cleanup);
                    Require(worker.Calls == 0 && errors.Errors.Count == 0, "Modelos chamados ou erros de binding: " + string.Join("\n", errors.Errors));
                    Console.WriteLine(cleanup ? "PASS: arquivamento WPF com Git real, recusa/mudança posterior, arquivo íntegro com ignorados/não rastreados, histórico/dependentes/reabertura e layout mínimo; zero erros de binding e nenhum modelo chamado. Capturas: " + output
                        : publish ? "PASS: publicação WPF com Git/processos reais, recusa/destino obsoleto, árvore/commit registrados, ignorados preservados, dependentes disponíveis, reabertura; zero erros de binding e nenhum modelo chamado. Capturas: " + output
                        : "PASS: editor/validação WPF, critérios literais/revisão, recusa/prévia obsoleta, Git e processos reais, histórico/logs/falha, cancelamento/tardio, navegação/reabertura e fechamento aguardado; zero erros de binding, nenhum modelo chamado. Capturas: " + output);
                    exitCode = 0; return;
                }
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
    private static async Task ValidateUiAsync(string output, WorkspaceWindow main, TaskDiffWindow diffWindow, SqliteWorkspaceStore store,
        ControlledRunner runner, WorkspaceProject project, byte[] indexBefore, string indexPath, bool publish, bool cleanup)
    {
        var vm = main.ViewModel;
        var editor = new ProjectValidationWindow(vm.CreateProjectValidation()) { Owner = main }; editor.Show();
        await Until(() => !editor.ViewModel.Busy); var config = editor.ViewModel;
        config.AddCommand.Execute(null); var draft = config.Selected!;
        draft.Name = "Conferir texto e eventos"; draft.Executable = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        draft.Arguments = JsonSerializer.Serialize(new[] { "-NoProfile", "-Command", "[Console]::WriteLine('saída conferida'); [Console]::Error.WriteLine('evento de teste')" });
        await config.SaveAsync(); Require(!config.IsDirty && (await store.GetProjectValidationAsync(project.Id)).Revision == 1, "Editor não salvou critérios.");
        Capture(editor, Path.Combine(output, "03-editor-normal.png")); editor.Width = editor.MinWidth; editor.Height = editor.MinHeight;
        Capture(editor, Path.Combine(output, "04-editor-minimum.png"));
        var literal = new ProjectValidationCommand("Argumentos literais", draft.Executable, ["", "espaço ação", "aspas \"", "linha\nnova"]);
        Require(ValidationCommandDraft.From(literal).ToCommand().Arguments.SequenceEqual(literal.Arguments), "Editor alterou argumentos literais.");
        var saved = await store.GetProjectValidationAsync(project.Id);
        await store.SaveProjectValidationAsync(saved); draft = config.Selected!; draft.Name = "Rascunho preservado"; await config.SaveAsync();
        Require(config.IsDirty && draft.Name == "Rascunho preservado" && config.Notice.Contains("mudou"), "Edição obsoleta apagou rascunho.");
        config.RevertCommand.Execute(null); await config.ReloadAsync();
        var validationWindow = new TaskValidationWindow(diffWindow.ViewModel.CreateValidationReview()) { Owner = main }; validationWindow.Show();
        var validation = validationWindow.ViewModel; await Until(() => !validation.Busy); validation.ConfirmValidation = _ => false;
        await validation.ValidateAsync(); Require(runner.Calls == 0 && validation.History.Count == 0, "Recusa iniciou processo.");
        validation.ConfirmValidation = preview => { store.SaveProjectValidationAsync(preview.Configuration).GetAwaiter().GetResult(); return true; };
        await validation.ValidateAsync(); Require(runner.Calls == 0 && validation.Notice.Contains("mudou"), "Prévia obsoleta iniciou comandos.");
        validation.ConfirmValidation = _ => true;
        Button(validationWindow, "ValidateTaskCombination").Command!.Execute(null); await Until(() => validation.Busy);
        await WaitValidationAsync(validation); Require(validation.History.Last().Record.State == ValidationState.Passed && validation.Details.Contains("saída conferida") && validation.Details.Contains("evento de teste"), "Processo real/logs não apresentados.");
        var indexAfter = SHA256.HashData(await File.ReadAllBytesAsync(indexPath));
        Require(indexBefore.SequenceEqual(indexAfter), "Validação modificou índice original.");
        var batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Require(!WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks), "Passed liberou dependente.");
        Capture(validationWindow, Path.Combine(output, "05-validation-normal.png")); validationWindow.Width = validationWindow.MinWidth; validationWindow.Height = validationWindow.MinHeight;
        Capture(validationWindow, Path.Combine(output, "06-validation-minimum.png"));
        Require(((TextBox)validationWindow.FindName("ValidationDetails")).ActualHeight > 180, "Logs inacessíveis no mínimo.");
        if (publish)
        {
            validation.ConfirmPublication = _ => false; await validation.PublishAsync();
            Require((await store.GetTaskPublicationsAsync(project.Id)).Count == 0, "Recusa publicou entrega.");
            var changed = Path.Combine(project.Directory, "posterior.txt");
            validation.ConfirmPublication = _ => { File.WriteAllText(changed, "destino mudou após prévia"); return true; };
            await validation.PublishAsync(); Require(validation.Publication is null && validation.Notice.Contains("alterações"), "Destino obsoleto publicado.");
            File.Delete(changed); validation.ConfirmPublication = _ => true;
            Button(validationWindow, "PublishTaskCombination").Command!.Execute(null); await Until(() => validation.Busy); await WaitValidationAsync(validation);
            Require(validation.Publication?.State == TaskPublicationState.Published && !validation.CanPublish, "Publicação não registrada ou duplicação habilitada.");
            var appliedText = await File.ReadAllTextAsync(Path.Combine(project.Directory, "portal.txt"));
            Require(appliedText.Replace("\r\n", "\n") == "Formulário acessível\n", "Árvore validada não aplicada.");
            Require(await File.ReadAllTextAsync(Path.Combine(project.Directory, "config.local")) == "arquivo original ignorado", "Ignorado original alterado.");
            batch = (await store.GetTaskBatchesAsync(project.Id)).Single(); Require(WorkspaceTaskPolicy.CanStart(batch.Tasks[1], batch.Tasks), "Publicação não liberou dependente.");
            Capture(validationWindow, Path.Combine(output, "07-published-minimum.png"));
            validationWindow.Close(); await Until(() => !validationWindow.IsVisible);
            validationWindow = new TaskValidationWindow(diffWindow.ViewModel.CreateValidationReview()) { Owner = main }; validationWindow.Show();
            await Until(() => !validationWindow.ViewModel.Busy && validationWindow.ViewModel.Publication is not null);
            Require(validationWindow.ViewModel.Publication?.State == TaskPublicationState.Published, "Reabertura perdeu publicação.");
            if (cleanup) await CleanupUiAsync(output, main, diffWindow, store, project);
            main.Close(); await Until(() => !main.IsVisible && !validationWindow.IsVisible && !editor.IsVisible); return;
        }
        saved = await store.GetProjectValidationAsync(project.Id);
        await store.SaveProjectValidationAsync(saved with { Commands = [saved.Commands[0] with { Arguments = ["-NoProfile", "-Command", "Write-Output 'falha conferida'; exit 9"] }, saved.Commands[0] with { Name = "Não executar" }] });
        await validation.ValidateAsync(); Require(validation.History.Last().Record.State == ValidationState.Failed && validation.History.Last().Record.Results?.Count == 1, "Falha não interrompeu sequência.");
        runner.Mode = 1; runner.Reset(); var late = validation.ValidateAsync(); await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(40));
        validation.CancelCommand.Execute(null); Require(validation.Busy, "Cancelamento liberou UI antes do processo."); runner.Release.SetResult(); await late;
        Require(validation.History.Last().Record.State == ValidationState.Cancelled, "Resposta tardia virou sucesso.");
        var other = Path.Combine(Path.GetDirectoryName(project.Directory)!, "Outro projeto validação"); Directory.CreateDirectory(other);
        await vm.AddProjectAsync(other); await Until(() => vm.CanConfigure);
        Require(validation.Project.Id == project.Id && config.Project.Id == project.Id, "Navegação mudou projeto dos painéis.");
        validationWindow.Close(); await Until(() => !validationWindow.IsVisible);
        validationWindow = new TaskValidationWindow(diffWindow.ViewModel.CreateValidationReview()) { Owner = main }; validationWindow.Show(); validation = validationWindow.ViewModel;
        await Until(() => !validation.Busy && validation.History.Count == 3); validation.ConfirmValidation = _ => true;
        runner.Mode = 2; runner.Reset(); var closing = validation.ValidateAsync(); await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(40));
        main.Close(); await closing; await Until(() => !main.IsVisible && !validationWindow.IsVisible && !editor.IsVisible);
        Require(runner.Cancelled && (await store.GetTaskIntegrationValidationsAsync(project.Id)).Last().State == ValidationState.Cancelled, "Fechamento deixou validação ativa.");
    }
    private static async Task CleanupUiAsync(string output, WorkspaceWindow main, TaskDiffWindow diffWindow, SqliteWorkspaceStore store, WorkspaceProject project)
    {
        var queue = new TaskQueueViewModel(store, main.ViewModel, project);
        var window = new TaskQueueWindow(queue) { Owner = main }; window.Show(); await queue.InitializeAsync();
        await Until(() => queue.CanArchiveWorktree); ((TabControl)window.FindName("TaskTabs")).SelectedIndex = 3; window.UpdateLayout();
        var button = Button(window, "ArchiveTaskWorktree"); Require(button.IsEnabled, "Arquivamento não vinculado ao botão.");
        queue.ConfirmArchive = _ => false; await queue.ArchiveWorktreeAsync(); Require((await store.GetTaskWorktreeCleanupsAsync(project.Id)).Count == 0, "Recusa registrou arquivamento.");
        var task = queue.SelectedTask!.Record; var path = Path.Combine(task.Worktree!.WorkingDirectory, "portal.txt"); var original = await File.ReadAllTextAsync(path);
        queue.ConfirmArchive = _ => { File.WriteAllText(path, "trabalho posterior à prévia"); return true; };
        await queue.ArchiveWorktreeAsync(); Require(queue.SelectedTask!.Record.Cleanup?.State == TaskWorktreeCleanupState.NeedsAttention && Directory.Exists(task.Worktree.CheckoutDirectory), "Mudança posterior movida.");
        await File.WriteAllTextAsync(path, original);
        await File.WriteAllTextAsync(Path.Combine(task.Worktree.CheckoutDirectory, "config.local"), "configuração da tarefa preservada");
        await File.WriteAllTextAsync(Path.Combine(task.Worktree.CheckoutDirectory, "rascunho ação.txt"), "arquivo não rastreado preservado");
        queue.ConfirmArchive = _ => true; button.Command!.Execute(null); await Until(() => queue.Busy);
        Require(!button.IsEnabled && !queue.CanReviewDiffs, "Outra operação permitida durante arquivamento.");
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90))) while (queue.Busy) await Task.Delay(25, timeout.Token);
        var archived = queue.SelectedTask!.Record.Cleanup!; Require(archived.State == TaskWorktreeCleanupState.Archived && !queue.CanArchiveWorktree && !queue.CanReviewDiffs, "Arquivamento não registrado ou ações do checkout disponíveis.");
        Require(await File.ReadAllTextAsync(Path.Combine(archived.Preview.ArchiveDirectory, "config.local")) == "configuração da tarefa preservada"
            && await File.ReadAllTextAsync(Path.Combine(archived.Preview.ArchiveDirectory, "rascunho ação.txt")) == "arquivo não rastreado preservado", "Arquivos locais não preservados.");
        Require(queue.WorktreeDetails.Contains(archived.Preview.ArchiveDirectory) && queue.Notice.Contains("ocupado"), "Caminho/limites omitidos.");
        await diffWindow.ViewModel.RefreshAsync(); Require(diffWindow.ViewModel.Review is null, "Painel antigo consultou checkout arquivado.");
        Capture(window, Path.Combine(output, "08-archived-normal.png")); window.Width = window.MinWidth; window.Height = window.MinHeight;
        await window.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Capture(window, Path.Combine(output, "09-archived-minimum.png")); Require(button.IsVisible && button.ActualWidth > 100, "Ação inacessível no mínimo.");
        window.Close(); await Until(() => !window.IsVisible);
        queue = new TaskQueueViewModel(store, main.ViewModel, project); window = new TaskQueueWindow(queue) { Owner = main }; window.Show(); await queue.InitializeAsync();
        Require(queue.SelectedTask?.Record.Cleanup?.State == TaskWorktreeCleanupState.Archived, "Reabertura perdeu arquivo/histórico.");
        queue.SelectedTask = queue.Tasks[1]; await Until(() => queue.CanStart); Require(queue.CanStart, "Dependente bloqueado após arquivamento.");
        window.Close();
    }
    private static async Task WaitValidationAsync(TaskValidationViewModel vm)
    { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90)); while (vm.Busy) await Task.Delay(25, stop.Token); await Task.Delay(30); }
    private sealed class ControlledRunner(IValidationCommandRunner native) : IValidationCommandRunner
    {
        public int Calls { get; private set; }
        public int Mode { get; set; }
        public bool Cancelled { get; private set; }
        public TaskCompletionSource Started { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reset() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); Cancelled = false; }
        public async Task<ValidationCommandResult> RunAsync(ProjectValidationCommand command, int index, string directory, CancellationToken token)
        {
            Calls++; if (Mode == 0) return await native.RunAsync(command, index, directory, token);
            var started = DateTimeOffset.UtcNow; Started.TrySetResult();
            if (Mode == 1) await Release.Task.WaitAsync(TimeSpan.FromSeconds(30));
            else try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { Cancelled = true; throw; }
            return new(index, ValidationState.Passed, 0, "SIMULAÇÃO: resultado tardio", "", false, started, DateTimeOffset.UtcNow);
        }
    }
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
