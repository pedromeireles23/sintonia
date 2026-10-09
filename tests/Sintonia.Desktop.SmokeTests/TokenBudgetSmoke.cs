using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;
using static Sintonia.Desktop.SmokeTests.TaskDiffSmoke;

namespace Sintonia.Desktop.SmokeTests;

internal static class TokenBudgetSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var projectPath = Path.Combine(output, "Projeto orçamento ação"); Directory.CreateDirectory(projectPath);
        var otherPath = Path.Combine(output, "Outro projeto"); Directory.CreateDirectory(otherPath);
        var database = Path.Combine(output, "run-" + Guid.NewGuid() + ".db"); var store = new SqliteWorkspaceStore(database); var worker = new Worker();
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors); PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var vm = new WorkspaceViewModel(store, new(store, [worker]), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var window = new WorkspaceWindow(vm); var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure); var project = vm.Project!;
                Require(vm.TokenLimit == "" && !vm.SaveSessionLimitCommand.CanExecute(null), "Limite padrão ou estado da ação inválido.");
                vm.TokenLimit = "inválido";
                try { await vm.SaveSessionLimitAsync(); throw new InvalidOperationException("Limite inválido salvo."); } catch (ArgumentException) { }
                Require(vm.TokenLimit == "inválido" && (await store.GetProjectExecutionSettingsAsync(project.Id)).Revision == 0, "Rascunho inválido perdido.");
                vm.TokenLimit = "100"; vm.TokenReservation = "60"; await vm.SaveSessionLimitAsync();
                vm.Prompt = "SIMULAÇÃO: medição"; vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(worker.Calls == 1 && vm.TokenBudgetDescription.Contains("70 tokens informados") && !vm.CanSend, "Consumo ou bloqueio visual incorreto.");
                vm.Prompt = "SIMULAÇÃO: bloqueado"; Require(!vm.SendCommand.CanExecute(null), "Chat permite outra reserva que não cabe.");
                vm.TokenReservation = "30"; await vm.SaveSessionLimitAsync(); Require(vm.CanSend, "Margem exata não liberou novo início.");
                vm.Prompt = "SIMULAÇÃO: ausente"; vm.SendCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(worker.Calls == 2 && vm.TokenBudgetDescription.Contains("1 execuções encerradas sem medição"), "Ausência virou zero garantido.");
                vm.Prompt = "SIMULAÇÃO: aguardar"; vm.SendCommand.Execute(null); await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await vm.RefreshTokenBudgetAsync(); Require(vm.TokenBudgetDescription.Contains("30 reservados"), "Reserva ativa não exibida.");
                var active = vm.SelectedConversation!; vm.NewConversationCommand.Execute(null); vm.Prompt = "SIMULAÇÃO: nova";
                Require(!vm.CanSend, "Reserva ativa não bloqueou outro chat."); vm.SelectedConversation = active; vm.CancelCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0); Require(vm.TokenBudgetDescription.Contains("2 execuções encerradas sem medição"), "Cancelamento perdeu lacuna.");
                var current = await store.GetProjectExecutionSettingsAsync(project.Id);
                await new SqliteWorkspaceStore(database).SaveProjectExecutionSettingsAsync(current with { MaxReportedTokens = 120 });
                vm.TokenLimit = "110";
                try { await vm.SaveSessionLimitAsync(); throw new InvalidOperationException("Revisão obsoleta salva."); } catch (InvalidOperationException error) when (error.Message.Contains("mudou")) { }
                Require(vm.TokenLimit == "110" && vm.TokenBudgetDescription.Contains("limite 120"), "Falha de revisão perdeu rascunho ou mostra limite antigo.");
                vm.TokenLimit = "100"; await vm.SaveSessionLimitAsync();
                await vm.AddProjectAsync(otherPath); await Until(() => vm.CanConfigure);
                Require(vm.TokenLimit == "" && vm.TokenBudgetDescription.Contains("0 tokens informados"), "Navegação herdou consumo/configuração.");
                vm.Project = project; await Until(() => vm.CanEditSessionLimit && vm.TokenLimit == "100");
                Require(vm.TokenReservation == "30" && vm.TokenBudgetDescription.Contains("70 tokens informados"), "Navegação perdeu consumo.");
                var sourceConversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Plano de teste", ProviderKind.Codex, null, null, "Conversa", "", ConversationAccess.ReadOnly);
                await store.SaveConversationAsync(sourceConversation);
                var plan = new PlanProposal(1, "Plano fornecido", "SIMULAÇÃO", Enumerable.Range(1, 2).Select(i => new ProposedTask("task" + i, "Tarefa " + i,
                    "Teste", ProviderKind.Codex, null, ConversationAccess.ReadOnly, "SIMULAÇÃO", ["."], [], ["Resultado"])).ToArray());
                var source = new ChatRun(Guid.NewGuid().ToString(), sourceConversation.Id, "Plano fornecido", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
                await store.BeginRunAsync(source); await store.FinishRunAsync(source with { State = ChatRunState.Completed,
                    Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```" }, sourceConversation, []);
                var proposal = await store.CreateProposalAsync(project.Id, source.Id); proposal = await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
                var batch = await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision);
                await vm.RefreshExecutionSettingsAsync(project);
                Require(vm.SelectAvailableTasks(project, batch).Count == 1, "Rodada excede margem disponível.");
                vm.TokenLimit = "70"; vm.TokenReservation = "20"; await vm.SaveSessionLimitAsync();
                Require(vm.SelectAvailableTasks(project, batch).Count == 0 && !vm.CanStartTask(project, batch.Tasks[0]), "Fila desconsiderou teto.");
                var expander = Descendants<Expander>(window).Single(); expander.IsExpanded = true; window.UpdateLayout();
                var scroll = Descendants<ScrollViewer>(expander).First(); scroll.ScrollToVerticalOffset(150); window.UpdateLayout();
                Capture(window, Path.Combine(output, "01-budget-normal.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Capture(window, Path.Combine(output, "02-budget-minimum.png"));
                Require(Button(window, "RefreshTokenBudget").IsVisible, "Atualização inacessível.");
                var reopened = new SqliteWorkspaceStore(database); await reopened.InitializeAsync();
                var budget = await reopened.GetProjectTokenBudgetAsync(project.Id);
                Require(budget.ReportedTokens == 70 && budget.Settings.MaxReportedTokens == 70 && budget.ReservedTokens == 0, "Reabertura perdeu limite/contagem.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                window.Close(); await Until(() => !window.IsVisible);
                Console.WriteLine("PASS: WPF orçamento por projeto, rascunho inválido/obsoleto, medição/ausência, reserva/cancelamento, bloqueio de chat/fila/rodada, navegação/reabertura e fechamento; zero erros de binding, provedores simulados e nenhum modelo chamado. Capturas: " + output); exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { await vm.StopAsync(); if (window.IsVisible) window.Close(); app.Shutdown(exitCode); }
        };
        app.Run(window); return exitCode;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Worker : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public int Calls { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++; progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO", "teste-" + Calls, "modelo-teste"));
            if (request.Prompt.Contains("aguardar")) { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            return new("teste-" + Calls, "modelo-teste", "SIMULAÇÃO: resposta", ConversationOutcome.Completed, [],
                request.Prompt.Contains("medição") ? new(ProviderKind.Codex, 60, 10, 70) : null);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
