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
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

internal static class ProjectTeamSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var firstPath = Path.Combine(output, "Portal ação"); var secondPath = Path.Combine(output, "Documentação interna");
        Directory.CreateDirectory(firstPath); Directory.CreateDirectory(secondPath);
        var sentinel = Path.Combine(firstPath, "preservado.txt"); File.WriteAllText(sentinel, "Arquivo do projeto");
        var store = new SqliteWorkspaceStore(Path.Combine(output, "team-" + Guid.NewGuid() + ".db"));
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var codex = new Worker(ProviderKind.Codex); var claude = new Worker(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [codex, claude]);
        var vm = new WorkspaceViewModel(store, service, app.Dispatcher, () => firstPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var main = new WorkspaceWindow(vm); FunctionProfileLibraryWindow? libraryWindow = null; ProposalReviewWindow? reviewWindow = null;
        var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(firstPath); await ChiefReady(vm);
                var first = vm.Project!;
                Require(vm.IsChief && vm.Access.Value == ConversationAccess.ReadOnly && vm.FunctionProfiles.All(p => p.ProjectId == first.Id), "Projeto não abriu com chefe/equipe próprios.");
                Require(FindButton(main, "RemoveProject").IsEnabled && !vm.CanEditConversationRole, "Lixeira/chefia não refletiram seu estado.");
                var library = await vm.LoadFunctionProfileLibraryAsync(_ => true);
                libraryWindow = new(library) { Owner = main }; libraryWindow.Show();
                library.SelectedProfile = library.Profiles.Single(p => p.FunctionName == "Desenvolvimento");
                library.Instructions = "REGRA A: validar o portal com suas ferramentas."; await library.SaveAsync();
                var developmentId = library.SelectedProfile!.Id;
                library.SelectedProfile = library.Profiles.Single(p => p.FunctionName == PlanProposalFormat.ChiefFunctionName);
                library.Provider = ProviderKind.Codex; library.Model = "modelo-chefe";
                library.Instructions = "Coordene as entregas deste portal."; await library.SaveAsync();
                await library.UseAsChiefAsync(); await ChiefReady(vm);
                var chiefId = vm.SelectedConversation!.Record.Id;
                Require(vm.Provider == ProviderKind.Codex && vm.Model == "modelo-chefe" && vm.Instructions == library.Instructions, "Perfil não configurou chefe.");
                Require(codex.Requests.Count + claude.Requests.Count == 0, "Configurar projeto chamou modelo.");
                libraryWindow.Width = libraryWindow.MinWidth; libraryWindow.Height = libraryWindow.MinHeight;
                Capture(libraryWindow, Path.Combine(output, "01-team-minimum.png"));
                libraryWindow.Close(); libraryWindow = null;

                await vm.AddProjectAsync(secondPath); await ChiefReady(vm); var second = vm.Project!;
                Require(vm.Provider == ProviderKind.Claude && vm.FunctionProfiles.All(p => p.Id != developmentId), "Outro projeto herdou edição da equipe.");
                var secondLibrary = await vm.LoadFunctionProfileLibraryAsync(_ => true);
                secondLibrary.SelectedProfile = secondLibrary.Profiles.Single(p => p.FunctionName == "Desenvolvimento");
                secondLibrary.Instructions = "REGRA B: apenas deste projeto."; await secondLibrary.SaveAsync();
                vm.Project = first; await ChiefReady(vm);
                Require(vm.SelectedConversation!.Record.Id == chiefId && vm.FunctionProfiles.Single(p => p.Id == developmentId).Instructions.StartsWith("REGRA A"), "Troca de projeto perdeu chefia/equipe.");

                vm.Prompt = "Olá chefe"; vm.SendCommand.Execute(null);
                await Until(() => codex.Requests.Count == 1 && vm.ActiveCount == 0);
                var native = vm.SelectedConversation.Record.NativeSessionId;
                Require(!vm.Notice.Contains("importada") && codex.Requests[0].Prompt.Contains("REGRA A") && !codex.Requests[0].Prompt.Contains("REGRA B"), "Conversa simples/contexto da equipe incorretos.");
                vm.Prompt = "Planejar portal"; vm.SendCommand.Execute(null);
                await Until(() => codex.Requests.Count == 2 && vm.ActiveCount == 0);
                Require(codex.Requests[^1].NativeSessionId == native, "Chefe não retomou a própria sessão.");
                var review = await vm.LoadProposalReviewAsync(); reviewWindow = new(review) { Owner = main }; reviewWindow.Show();
                Require(review.Tasks.Count == 2 && (await store.GetTaskBatchesAsync(first.Id)).Count == 0, "Plano executou antes da confirmação.");
                Capture(reviewWindow, Path.Combine(output, "02-review.png"));
                await review.SaveAsync(ProposalReviewState.Draft);
                Require(codex.Requests.Count == 2 && claude.Requests.Count == 0, "Salvar rascunho iniciou agentes.");
                codex.HoldTasks = true;
                await review.ApproveAndDelegateAsync();
                await Until(() => codex.Requests.Count == 3 && vm.ActiveCount == 1);
                using (var liveQueue = await vm.LoadTaskQueueAsync(first))
                {
                    Require(liveQueue.CancelCommand.CanExecute(null), "Fila não permite cancelar tarefa delegada pelo chefe.");
                    liveQueue.CancelCommand.Execute(null);
                    await Until(() => vm.ActiveCount == 0 && liveQueue.SelectedTask!.Record.State == WorkspaceTaskState.Cancelled && liveQueue.CanStart);
                    codex.ReleaseTasks.TrySetResult();
                    await liveQueue.StartAsync();
                    await Until(() => vm.ActiveCount == 0 && liveQueue.CanReview);
                    Require(liveQueue.SelectedTask!.Record.State == WorkspaceTaskState.AwaitingReview, "Fila não atualizou entrega delegada automaticamente.");
                }
                var batch = (await store.GetTaskBatchesAsync(first.Id)).Single();
                Require(batch.Tasks[0].State == WorkspaceTaskState.AwaitingReview && batch.Tasks[1].Attempts == 0, "Dependência iniciou antes da revisão.");
                await review.ApproveAndDelegateAsync(); Require(codex.Requests.Count == 4, "Confirmação repetida duplicou execução.");
                await store.ReviewTaskAsync(first.Id, batch.Tasks[0].Id, batch.Tasks[0].LastRunId!, true, "Entrega conferida");
                using (var queue = await vm.LoadTaskQueueAsync(first))
                {
                    Require(queue.CanStartAvailable, "Revisão não liberou dependente.");
                    await queue.StartAvailableAsync();
                }
                Require(claude.Requests.Count == 1 && claude.Requests[0].WorkingDirectory == firstPath, "Delegação perdeu projeto/provedor.");
                reviewWindow.Close(); reviewWindow = null;
                await vm.OpenChiefAsync(); await ChiefReady(vm);
                vm.Prompt = "Como está a equipe?"; vm.SendCommand.Execute(null);
                await Until(() => codex.Requests.Count == 5 && vm.ActiveCount == 0);
                Require(codex.Requests[^1].Prompt.Contains("AwaitingReview") && codex.Requests[^1].Prompt.Contains("RESPOSTA DE TESTE"), "Chefe não recebeu estado/resultados.");
                Capture(main, Path.Combine(output, "03-chief-normal.png"));
                main.Width = main.MinWidth; main.Height = main.MinHeight; Capture(main, Path.Combine(output, "04-chief-minimum.png"));

                vm.NewConversationCommand.Execute(null); Require(!vm.IsChief, "Nova conversa não liberou conversa independente.");
                await vm.OpenChiefAsync(); await ChiefReady(vm); Require(vm.SelectedConversation!.Record.Id == chiefId, "Chat do chefe perdeu identidade.");
                vm.Prompt = "aguardar"; vm.SendCommand.Execute(null); await Until(() => codex.Waiting);
                Require(!vm.CanRemoveProject && !FindButton(main, "RemoveProject").IsEnabled, "Lixeira permitiu remover projeto ativo.");
                vm.CancelCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                await vm.RemoveProjectAsync(first); await ChiefReady(vm);
                Require(vm.Project!.Id == second.Id && File.ReadAllText(sentinel) == "Arquivo do projeto", "Remoção perdeu arquivo/seleção.");
                await vm.AddProjectAsync(firstPath); await ChiefReady(vm);
                Require(vm.SelectedConversation!.Record.Id == chiefId && vm.SelectedConversation.Record.NativeSessionId == native, "Readicionar não restaurou o chefe/histórico.");
                await vm.RemoveProjectAsync(first); await ChiefReady(vm); await vm.RemoveProjectAsync(second);
                await Until(() => vm.Project is null && vm.Conversations.Count == 0);
                Require(vm.FunctionProfiles.Count == 0 && !vm.CanSend && !vm.CanRemoveProject, "Última remoção deixou dados do projeto anterior.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                await vm.StopAsync(); main.Close();
                Console.WriteLine("PASS: chefia principal persistente, equipes isoladas, modelo/instruções por projeto, conversa natural, contexto/resultados, plano confirmado antes da delegação, dependências/revisão, cancelamento, lixeira e restauração; zero erros de binding. Provedores de teste; nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                await vm.StopAsync();
                libraryWindow?.Close(); reviewWindow?.Close(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static Task ChiefReady(WorkspaceViewModel vm) => Until(() => vm.IsProjectChief && vm.CanEditFunction && vm.SelectedConversation!.Loaded);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado da equipe não chegou."); await Task.Delay(25); }
    }
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
    private sealed class Worker(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public bool Waiting { get; private set; }
        public bool HoldTasks { get; set; }
        public TaskCompletionSource ReleaseTasks { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var native = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: equipe de teste", native, request.Model ?? "modelo-teste"));
            if (request.Prompt.StartsWith("aguardar", StringComparison.Ordinal)) { Waiting = true; await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            if (HoldTasks && !request.Instructions.Contains("sintonia-plan", StringComparison.Ordinal)) await ReleaseTasks.Task.WaitAsync(token);
            var text = "SIMULAÇÃO: RESPOSTA DE TESTE. Nenhum arquivo modificado.";
            if (request.Prompt.StartsWith("Planejar", StringComparison.Ordinal)) text += "\n```sintonia-plan\n" + PlanProposalFormat.Serialize(new(1, "Portal de teste", "Conferir duas entregas.",
                [new("analyze", "Analisar portal", "Análise", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "Conferir referências.", ["."], [], ["Fontes identificadas."]),
                 new("review", "Revisar análise", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly, "Revisar a análise.", ["."], ["analyze"], ["Critérios conferidos."])])) + "\n```";
            await Task.Delay(50, token); return new(native, request.Model ?? "modelo-teste", text, ConversationOutcome.Completed, []);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
