using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;
using static Sintonia.Desktop.SmokeTests.TaskDiffSmoke;

namespace Sintonia.Desktop.SmokeTests;

internal static class ChiefPlanSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output); var projectPath = Path.Combine(output, "Projeto acompanhamento ação"); Directory.CreateDirectory(projectPath);
        var database = Path.Combine(output, "run-" + Guid.NewGuid() + ".db"); var store = new SqliteWorkspaceStore(database); var provider = new Worker();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [provider]), app.Dispatcher, () => projectPath, (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var main = new WorkspaceWindow(vm); TaskQueueWindow? queueWindow = null; ProposalReviewWindow? reviewWindow = null; var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure); var project = vm.Project!;
                vm.TaskAttemptLimit = 2; vm.ExecutionTimeout = "90"; await vm.SaveSessionLimitAsync();
                Require((await store.GetProjectExecutionSettingsAsync(project.Id)).MaxAttempts == 2 && (await store.GetProjectExecutionSettingsAsync(project.Id)).MaxExecutionSeconds == 90, "Limites adicionais não salvos.");
                vm.ExecutionTimeout = "prazo inválido";
                try { await vm.SaveSessionLimitAsync(); throw new InvalidOperationException("Prazo inválido foi salvo."); }
                catch (ArgumentException) { }
                Require(vm.ExecutionTimeout == "prazo inválido" && (await store.GetProjectExecutionSettingsAsync(project.Id)).MaxExecutionSeconds == 90, "Prazo inválido descartou rascunho ou alterou configuração."); vm.ExecutionTimeout = "90";
                vm.Function = vm.Functions.Single(f => f.Name == PlanProposalFormat.ChiefFunctionName); vm.Prompt = "Planejar relatório (SIMULAÇÃO)"; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0); var chiefId = vm.SelectedConversation!.Record.Id;
                var review = await vm.LoadProposalReviewAsync(); await review.SaveAsync(ProposalReviewState.Approved);
                var queue = await vm.LoadTaskQueueAsync(); queueWindow = new(queue) { Owner = main }; queueWindow.Show(); await queue.EnqueueAsync();
                await Until(() => queue.CanStart); await queue.StartAsync(); Require(queue.SelectedTask!.Record.State == WorkspaceTaskState.AwaitingReview, "Tarefa não aguardou revisão humana.");
                var original = queue.SelectedBatch!; var firstId = original.Tasks[0].Id; var firstSession = original.Tasks[0].ConversationId;
                var oversized = new string('x', 200_000); vm.Prompt = oversized;
                try { await queue.PrepareChiefAsync(); throw new InvalidOperationException("Pedido extenso foi preparado."); }
                catch (InvalidOperationException error) when (error.Message.Contains("excedem")) { }
                Require(vm.Prompt == oversized && provider.Requests.Count == 2, "Acompanhamento extenso perdeu rascunho ou chamou modelo.");
                vm.Prompt = "Orientação preservada"; await queue.PrepareChiefAsync();
                Require(provider.Requests.Count == 2 && vm.Prompt.StartsWith("Orientação preservada") && vm.Prompt.Contains("savedStatus") && vm.Prompt.Contains("AwaitingReview") && vm.Prompt.Contains("resultado da tarefa"), "Acompanhamento omitiu estado/resultado ou enviou automaticamente.");
                Require(vm.SelectedConversation?.Record.Id == chiefId && vm.Access.Value == ConversationAccess.ReadOnly, "Chefia não retomada em leitura.");
                vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(provider.Requests.Count == 3 && provider.Requests.Last().NativeSessionId == "chefe-teste" && provider.Requests.Last().PermissionHandler is null, "Atualização não retomou chefe ou ofereceu escrita.");
                review = await vm.LoadProposalReviewAsync(); reviewWindow = new(review) { Owner = main }; reviewWindow.Show(); Require(review.Tasks.Count == 3, "Proposta atualizada não importada.");
                await review.SaveAsync(ProposalReviewState.Approved); await queue.InitializeAsync();
                queue.ConfirmRevision = _ => false; await queue.ApplyRevisionAsync(); Require(queue.SelectedBatch!.Revision == 0, "Recusa atualizou fila.");
                ((TabItem)queueWindow.FindName("ChiefPlanTab")).IsSelected = true; queueWindow.UpdateLayout();
                queue.ConfirmRevision = _ => true; Button(queueWindow, "ApplyPlanRevision").Command!.Execute(null); await Until(() => !queue.Busy && queue.SelectedBatch!.Revision == 1);
                var updated = queue.SelectedBatch!; Require(updated.Tasks.Count == 3 && updated.Tasks[0].Id == firstId && updated.Tasks[0].ConversationId == firstSession && updated.Tasks[0].Attempts == 1 && updated.Tasks[0].State == WorkspaceTaskState.AwaitingReview, "Revisão perdeu sessão/tentativa/resultado iniciado.");
                Require(updated.Tasks[1].Definition.Title == "Revisar conclusão" && queue.ApprovedProposals.Count == 0 && provider.Requests.Count == 3, "Atualização duplicou execução ou não aplicou proposta.");
                await review.InitializeAsync(); Require(review.IsQueued && !review.CanEdit, "Proposta aplicada permaneceu editável.");
                queueWindow.UpdateLayout(); Capture(queueWindow, Path.Combine(output, "01-chief-plan-normal.png"));
                queueWindow.Width = queueWindow.MinWidth; queueWindow.Height = queueWindow.MinHeight; Capture(queueWindow, Path.Combine(output, "02-chief-plan-minimum.png"));
                Require(Button(queueWindow, "PrepareChiefFollowUp").IsVisible && Button(queueWindow, "ApplyPlanRevision").IsVisible, "Ações inacessíveis no mínimo.");
                var chiefTab = (TabItem)queueWindow.FindName("ChiefPlanTab"); ((ScrollViewer)chiefTab.Content).ScrollToBottom(); queueWindow.UpdateLayout(); Capture(queueWindow, Path.Combine(output, "03-chief-plan-minimum-actions.png"));
                queueWindow.Close(); await Until(() => !queueWindow.IsVisible); queue = await vm.LoadTaskQueueAsync(); queueWindow = new(queue) { Owner = main }; queueWindow.Show();
                Require(queue.SelectedBatch!.Revision == 1 && queue.SelectedBatch.Tasks.Count == 3, "Reabertura perdeu plano atualizado.");
                var reopened = new SqliteWorkspaceStore(database); await reopened.InitializeAsync(); var settings = await reopened.GetProjectExecutionSettingsAsync(project.Id);
                Require(settings.MaxAttempts == 2 && settings.MaxExecutionSeconds == 90, "Reabertura perdeu limites.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                main.Close(); await Until(() => !main.IsVisible && !queueWindow.IsVisible);
                Console.WriteLine("PASS: acompanhamento da chefia no chat, rascunho preservado, retomada/leitura, revisão/recusa e aplicação às tarefas não iniciadas, sessões/resultados preservados, limites de tentativas/tempo e reabertura; zero erros de binding, provedores simulados e nenhum modelo chamado. Capturas: " + output); exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                if (queueWindow is not null) { await queueWindow.ViewModel.StopAsync(); if (queueWindow.IsVisible) queueWindow.Close(); }
                if (reviewWindow?.IsVisible == true) { reviewWindow.ViewModel.RevertCommand.Execute(null); reviewWindow.Close(); }
                await vm.StopAsync(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Worker : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public List<ConversationRequest> Requests { get; } = [];
        private static ProposedTask First() => new("write", "Produzir relatório", "Documentação", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "Produzir relatório", ["."], [], ["Resultado revisável"]);
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request); var chief = request.Instructions.Contains("sintonia-plan"); var native = chief ? "chefe-teste" : "tarefa-teste";
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão", native, "modelo-teste"));
            if (!chief) return Task.FromResult(new ConversationResult(native, "modelo-teste", "SIMULAÇÃO: resultado da tarefa para revisão humana", ConversationOutcome.Completed, []));
            var followUp = request.Prompt.Contains("savedStatus");
            var second = new ProposedTask("review", followUp ? "Revisar conclusão" : "Revisar relatório", "Revisão", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "Conferir texto", ["."], ["write"], ["Revisão verificável"]);
            var plan = new PlanProposal(1, "Plano de relatório", "Produzir relatório e revisão", followUp ? [First(), second, second with { Id = "extra", Title = "Conferência final", Dependencies = ["review"] }] : [First(), second]);
            return Task.FromResult(new ConversationResult(native, "modelo-teste", "SIMULAÇÃO\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```", ConversationOutcome.Completed, []));
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
