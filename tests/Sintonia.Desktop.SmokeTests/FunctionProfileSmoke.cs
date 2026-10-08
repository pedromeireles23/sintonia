using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

internal static class FunctionProfileSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var projectPath = Path.Combine(output, "Projeto geral ação"); Directory.CreateDirectory(projectPath);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db"); var store = new SqliteWorkspaceStore(database);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var codex = new Worker(ProviderKind.Codex); var claude = new Worker(ProviderKind.Claude);
        var service = new WorkspaceChatService(store, [codex, claude]);
        var vm = new WorkspaceViewModel(store, service, app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var main = new WorkspaceWindow(vm); FunctionProfileLibraryWindow? window = null;
        var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure);
                var project = vm.Project!; var allowDelete = false; var confirmations = 0;
                var library = await vm.LoadFunctionProfileLibraryAsync(_ => { confirmations++; return allowDelete; });
                window = new(library) { Owner = main }; window.Show();
                library.Name = "Analista de requisitos"; library.FunctionName = "Análise de requisitos"; library.Provider = ProviderKind.Claude;
                library.Model = "modelo-configurado"; library.Instructions = "Conferir as fontes e registrar evidências.\nManter literal `$(texto)` e instruções do projeto.";
                await library.SaveAsync(); var original = library.SelectedProfile!;
                Require(vm.FunctionProfiles.Count == 1 && !library.IsDirty && library.CanChoose, "Perfil não chegou à central.");
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                Capture(window, Path.Combine(output, "01-profile-minimum.png"));
                Require(claude.Requests.Count + codex.Requests.Count == 0, "Salvar perfil chamou modelo.");
                vm.Access = vm.AccessOptions[1]; vm.Prompt = "Analisar este projeto."; await vm.ApplyFunctionProfileAsync();
                Require(vm.Provider == ProviderKind.Claude && vm.Model == original.Model && vm.Function.Name == original.FunctionName
                    && vm.Instructions == original.Instructions && vm.Access.Value == ConversationAccess.ReadOnly && vm.Prompt == "Analisar este projeto.", "Padrões/rascunho incorretos ao aplicar.");
                vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                var session = vm.SelectedConversation!; var snapshot = session.Record;
                Require(claude.Requests[0].Instructions == original.Instructions && claude.Requests[0].Model == original.Model,
                    "Modelo/instruções não chegaram ao provedor.");
                var batch = await SeedTaskAsync(store, project, original);
                var stale = new FunctionProfileLibraryViewModel(store, vm.RefreshFunctionProfilesAsync, _ => true); await stale.InitializeAsync();
                library.Name = "Documentação de referência"; library.FunctionName = "Documentação"; library.Provider = ProviderKind.Codex;
                library.Model = "outro-modelo"; library.Instructions = "Organizar referências verificáveis."; await library.SaveAsync();
                Require(session.Record == snapshot && vm.Provider == snapshot.Provider && vm.Instructions == snapshot.Instructions,
                    "Editar perfil alterou a conversa existente.");
                stale.Instructions = "Alteração obsoleta";
                try { await stale.SaveAsync(); throw new InvalidOperationException("Editor obsoleto sobrescreveu o perfil."); }
                catch (InvalidOperationException exception) when (exception.Message.Contains("outra janela")) { }
                Require(stale.IsDirty && !stale.CanChoose, "Edição local obsoleta foi descartada.");
                library.NewCommand.Execute(null); library.Name = "DOCUMENTAÇÃO DE REFERÊNCIA"; library.FunctionName = "Revisão";
                try { await library.SaveAsync(); throw new InvalidOperationException("Nome duplicado salvo."); }
                catch (ArgumentException exception) when (exception.Message.Contains("Já existe")) { }
                Require(library.IsDirty && !library.NewCommand.CanExecute(null), "Rascunho inválido não foi protegido.");
                library.RevertCommand.Execute(null); await library.InitializeAsync();
                vm.Prompt = "Outro rascunho"; await vm.ApplyFunctionProfileAsync();
                Require(vm.SelectedConversation is null && vm.Provider == ProviderKind.Codex && vm.Model == "outro-modelo"
                    && vm.Prompt == "Outro rascunho", "Perfil editado não preencheu nova conversa.");
                var otherPath = Path.Combine(output, "Segundo projeto"); Directory.CreateDirectory(otherPath);
                await vm.AddProjectAsync(otherPath); await Until(() => vm.CanConfigure); await vm.ApplyFunctionProfileAsync();
                vm.Prompt = "Documentar segundo projeto."; vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(codex.Requests[^1].WorkingDirectory == otherPath && codex.Requests[^1].Instructions == library.Instructions,
                    "Perfil não foi reutilizado no outro projeto.");
                vm.Project = project; await Until(() => vm.Conversations.Any(c => c.Record.Id == session.Record.Id));
                vm.SelectedConversation = session; vm.Prompt = "Retomar análise.";
                await Until(() => vm.CanSend);
                vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(claude.Requests[^1].NativeSessionId == snapshot.NativeSessionId && claude.Requests[^1].Instructions == original.Instructions
                    && session.Record.FunctionName == original.FunctionName, "Retomada perdeu função/instruções originais.");
                await library.DeleteAsync(); Require(confirmations == 1 && vm.FunctionProfiles.Count == 1, "Recusar exclusão removeu o perfil.");
                allowDelete = true; await library.DeleteAsync();
                Require(vm.FunctionProfiles.Count == 0 && session.Record.Instructions == original.Instructions, "Excluir perfil alterou conversa.");
                var savedBatch = (await store.GetTaskBatchesAsync(project.Id)).Single(b => b.Id == batch.Id);
                Require(savedBatch.Tasks[0].Definition.Instructions == original.Instructions && savedBatch.Tasks[0].Definition.Provider == original.Provider,
                    "Editar/excluir perfil alterou a tarefa copiada.");
                await service.SendTaskAsync(project.Id, savedBatch.Tasks[0].Id, new SilentProgress(), CancellationToken.None);
                Require(claude.Requests[^1].Instructions == original.Instructions, "Fila perdeu instruções após exclusão do perfil.");
                library.NewCommand.Execute(null); library.Name = "Coordenação geral"; library.FunctionName = PlanProposalFormat.ChiefFunctionName;
                library.Provider = ProviderKind.Claude; library.Instructions = "Planeje documentação e análise para este projeto geral.";
                Require(library.InstructionLimit == PlanProposalFormat.ChiefAdditionalInstructionsLimit, "Limite de chefia incorreto.");
                await library.SaveAsync(); await vm.ApplyFunctionProfileAsync(); vm.Access = vm.AccessOptions[1]; vm.Prompt = "Planejar documentação.";
                vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(vm.Access.Value == ConversationAccess.ReadOnly && claude.Requests[^1].PermissionHandler is null
                    && claude.Requests[^1].Instructions.Contains("sintonia-plan") && vm.SelectedConversation!.Record.Instructions == library.Instructions,
                    "Perfil da chefia elevou acesso ou perdeu o contrato.");
                var reopenedStore = new SqliteWorkspaceStore(database); await reopenedStore.InitializeAsync();
                var restored = new WorkspaceViewModel(reopenedStore, new(reopenedStore, [codex, claude]), app.Dispatcher, () => projectPath,
                    (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
                await restored.InitializeAsync(); Require(restored.FunctionProfiles.Single().Name == "Coordenação geral", "Perfil não sobreviveu à reabertura.");
                restored.Project = restored.Projects.Single(p => p.Id == project.Id);
                await Until(() => restored.Conversations.Any(c => c.Record.Id == snapshot.Id));
                restored.SelectedConversation = restored.Conversations.Single(c => c.Record.Id == snapshot.Id);
                await Until(() => restored.CanEditFunction && restored.SelectedConversation.Loaded);
                Require(restored.Function.Name == original.FunctionName && restored.Instructions == original.Instructions,
                    "Reabrir o aplicativo perdeu a função personalizada da conversa.");
                await restored.StopAsync();
                vm.SelectedConversation = session; vm.Prompt = "aguardar"; vm.SendCommand.Execute(null); await Until(() => claude.Waiting);
                library.Instructions += " Preserve as fontes."; await library.SaveAsync();
                Require(session.Running && vm.ActiveCount == 1, "Editar perfil interrompeu execução ativa.");
                vm.Prompt = "Novo planejamento"; await vm.ApplyFunctionProfileAsync();
                Require(session.Running && vm.SelectedConversation is null && vm.Prompt == "Novo planejamento" && vm.ActiveCount == 1,
                    "Aplicar perfil alterou a execução ativa ou enviou mensagem.");
                vm.SelectedConversation = session;
                vm.CancelCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(session.State == "Cancelada", "Execução não cancelou.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                window.Close(); window = null; await vm.StopAsync(); main.Close();
                Console.WriteLine("PASS: biblioteca de perfis, padrões, dois projetos, sessão retomada, snapshots, exclusão confirmada, revisão concorrente, chefia em leitura e reabertura; zero erros de binding. Provedores de teste; nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                await vm.StopAsync();
                if (window is not null) { window.ViewModel.RevertCommand.Execute(null); if (!window.ViewModel.Busy) window.Close(); }
                if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task<WorkspaceTaskBatch> SeedTaskAsync(SqliteWorkspaceStore store, WorkspaceProject project, WorkspaceFunctionProfile profile)
    {
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Planejamento de teste", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly); await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Análise de projeto", "Conferir referências.",
            [new("analyze", "Analisar referências", profile.FunctionName, profile.Provider, profile.Model, ConversationAccess.ReadOnly, profile.Instructions, ["docs/"], [], ["Fontes verificáveis."])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Plano fornecido pelo teste", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "SIMULAÇÃO: plano fornecido.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```", FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id); proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
        return await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado de perfis não chegou."); await Task.Delay(25); }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class SilentProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
    private sealed class Worker(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public bool Waiting { get; private set; }
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var native = request.NativeSessionId ?? Guid.NewGuid().ToString(); var model = request.Model ?? "modelo-teste";
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: provedor de teste de perfis", native, model));
            if (request.Prompt == "aguardar") { Waiting = true; await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            var text = "SIMULAÇÃO: instruções do perfil recebidas. Nenhum arquivo modificado.";
            if (request.Instructions.Contains("sintonia-plan")) text += "\n```sintonia-plan\n" + PlanProposalFormat.Serialize(new(1, "Documentação", "Registrar fontes.",
                [new("docs", "Registrar fontes", "Documentação", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "Conferir referências.", ["docs/"], [], ["Fontes identificadas."])])) + "\n```";
            await Task.Delay(25, token); return new(native, model, text, ConversationOutcome.Completed, []);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
