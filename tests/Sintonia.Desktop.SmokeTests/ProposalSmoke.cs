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

internal static class ProposalSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var projectPath = Path.Combine(output, "Projeto de planos ação"); Directory.CreateDirectory(projectPath);
        var secondPath = Path.Combine(output, "Outro projeto"); Directory.CreateDirectory(secondPath);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var provider = new PlanningProvider(); var store = new SqliteWorkspaceStore(database);
        var vm = new WorkspaceViewModel(store, new(store, [provider]), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var window = new WorkspaceWindow(vm); ProposalReviewWindow? reviewWindow = null;
        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure);
                var project = vm.Project!;
                vm.Function = vm.Functions.Single(f => f.Name == PlanProposalFormat.ChiefFunctionName);
                vm.Access = vm.AccessOptions[1];
                Require(vm.Access.Value == ConversationAccess.ReadOnly && !vm.CanEditAccess, "Chefia permitiu escrita na configuração.");
                vm.Prompt = "Planejar menu do jogo (provedor de teste)."; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                Require(provider.Requests.Count == 1 && provider.Requests[0].Access == ConversationAccess.ReadOnly
                    && provider.Requests[0].PermissionHandler is null && provider.Requests[0].Instructions.Contains("sintonia-plan"), "Pedido de chefia incompatível.");
                var chief = vm.SelectedConversation!;
                var review = await vm.LoadProposalReviewAsync(); reviewWindow = new(review) { Owner = window }; reviewWindow.Show();
                Require(review.Proposals.Count == 1 && review.Tasks.Count == 2, "Proposta não chegou à revisão.");
                Capture(reviewWindow, Path.Combine(output, "01-proposal.png"));
                var second = review.Tasks[1]; review.SelectedTask = second;
                second.Dependencies = second.Id; review.ValidateCommand.Execute(null);
                Require(review.Notice.Contains("Dependências inválidas"), "Dependência inválida não foi apresentada.");
                try { await review.SaveAsync(ProposalReviewState.Approved); throw new InvalidOperationException("Plano inválido foi aprovado."); }
                catch (PlanValidationException) { }
                Require((await store.GetProposalsAsync(project.Id)).Single().State == ProposalReviewState.Draft, "Aprovação inválida alterou o banco.");
                second.Dependencies = "task-1"; second.Provider = ProviderKind.Codex; second.Model = "modelo-teste";
                second.FunctionName = "Revisão técnica"; second.AcceptanceCriteria = "Botão acessível pelo teclado.";
                review.Title = "Plano revisado pelo usuário";
                await review.SaveAsync(ProposalReviewState.Draft);
                var stale = new ProposalReviewViewModel(store, project); await stale.InitializeAsync();
                await review.SaveAsync(ProposalReviewState.Approved);
                Require(!review.IsDirty && provider.Requests.Count == 1, "Confirmar plano disparou uma tarefa.");
                var approved = (await store.GetProposalsAsync(project.Id)).Single();
                Require(approved.State == ProposalReviewState.Approved && approved.Definition.Tasks[1].Provider == ProviderKind.Codex
                    && approved.Definition.Tasks[1].Model == "modelo-teste", "Edição/aprovação não foi salva.");
                stale.Title = "Alteração obsoleta";
                try { await stale.SaveAsync(ProposalReviewState.Draft); throw new InvalidOperationException("Revisão antiga sobrescreveu o plano."); }
                catch (InvalidOperationException exception) when (exception.Message.Contains("outra janela")) { }
                review.Title = "Em ajuste"; Require(!review.CanChooseProposal, "Edições não salvas podem ser descartadas ao trocar de plano.");
                await review.SaveAsync(ProposalReviewState.Draft);
                Require((await store.GetProposalsAsync(project.Id)).Single().State == ProposalReviewState.Draft, "Editar manteve aprovação anterior.");
                review.AddTaskCommand.Execute(null); var added = review.SelectedTask!;
                added.Title = "Conferir resultado"; added.Instructions = "Revisar o menu."; added.Scope = "src/";
                added.AcceptanceCriteria = "Resultado conferido."; added.Dependencies = "task-2";
                review.ValidateCommand.Execute(null); Require(review.Notice.Contains("Plano válido"), "Tarefa acrescentada não é válida.");
                review.RemoveTaskCommand.Execute(null); await review.SaveAsync(ProposalReviewState.Approved);
                reviewWindow.Width = reviewWindow.MinWidth; reviewWindow.Height = reviewWindow.MinHeight; reviewWindow.UpdateLayout();
                Capture(reviewWindow, Path.Combine(output, "02-proposal-minimum.png"));
                reviewWindow.Close(); reviewWindow = null;
                await vm.AddProjectAsync(secondPath); await Until(() => vm.CanConfigure);
                Require((await vm.LoadProposalReviewAsync()).Proposals.Count == 0, "Planos misturados entre projetos.");
                vm.Project = project; await Until(() => vm.SelectedConversation?.Record.Id == chief.Record.Id);
                vm.Prompt = "invalid"; vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(chief.State == "Concluída" && vm.Notice.Contains("não pôde ser importada"), "Plano inválido alterou o resultado do chat.");
                var restored = await vm.LoadProposalReviewAsync();
                Require(restored.Proposals.Count == 1 && restored.SelectedProposal!.State == ProposalReviewState.Approved,
                    "Reimportação duplicou ou sobrescreveu o plano aprovado.");
                var reopenedStore = new SqliteWorkspaceStore(database); await reopenedStore.InitializeAsync();
                var reopened = new ProposalReviewViewModel(reopenedStore, project); await reopened.InitializeAsync();
                Require(reopened.SelectedProposal!.Definition.Title == "Em ajuste" && reopened.Tasks.Count == 2,
                    "Plano revisado não sobreviveu à reabertura.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                await vm.StopAsync(); window.Close();
                Console.WriteLine("PASS: chefia em leitura, proposta, edição, validação, confirmação sem execução, revisão concorrente, dois projetos e reabertura; zero erros de binding. Provedor de teste; nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                if (reviewWindow is not null) { reviewWindow.ViewModel.RevertCommand.Execute(null); reviewWindow.Close(); }
                if (window.IsVisible) window.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(window); return exitCode;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado de proposta não chegou."); await Task.Delay(25); }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class PlanningProvider : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public List<ConversationRequest> Requests { get; } = [];
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: planejamento de teste.", id, "modelo-teste"));
            var plan = new PlanProposal(1, "Menu inicial", "Criar um menu acessível.",
                [new("task-1", "Criar menu", "Interface", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite,
                    "Criar menu inicial.", ["src/"], [], ["Botão inicia o jogo."]),
                 new("task-2", "Revisar menu", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly,
                    "Revisar acessibilidade.", ["src/"], ["task-1"], ["Navegação por teclado."])]);
            var text = request.Prompt == "invalid" ? "SIMULAÇÃO: resposta sem proposta válida." : "SIMULAÇÃO: plano para revisão.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```";
            return Task.FromResult(new ConversationResult(id, "modelo-teste", text, ConversationOutcome.Completed, []));
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
