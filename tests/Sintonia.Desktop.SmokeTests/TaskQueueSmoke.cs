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

internal static class TaskQueueSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var projectPath = Path.Combine(output, "Portal de atendimento ação"); Directory.CreateDirectory(projectPath);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var store = new SqliteWorkspaceStore(database);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var codex = new Worker(ProviderKind.Codex); var claude = new Worker(ProviderKind.Claude);
        var vm = new WorkspaceViewModel(store, new(store, [codex, claude]), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var main = new WorkspaceWindow(vm); TaskQueueWindow? window = null;
        var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await Until(() => vm.CanConfigure);
                var project = vm.Project!; var proposal = await SeedAsync(store, project);
                var queue = await vm.LoadTaskQueueAsync(); window = new(queue) { Owner = main }; window.Show();
                Require(queue.ApprovedProposals.Count == 1 && queue.Tasks.Count == 0, "Proposta aprovada não apareceu.");
                await queue.EnqueueAsync(); Require(queue.Tasks.Count == 3 && codex.Requests.Count == 0, "Encaminhar executou ou perdeu tarefas.");
                var planReview = await vm.LoadProposalReviewAsync();
                Require(planReview.IsQueued && !planReview.CanEdit && planReview.CanSelectTask, "Plano encaminhado ainda oferece edição ou impede consulta das tarefas.");
                var first = queue.Tasks[0].Record; var second = queue.Tasks[1].Record; var third = queue.Tasks[2].Record;
                queue.SelectedTask = queue.Tasks[1]; await Until(() => queue.Session?.Record.Id == second.ConversationId);
                Require(!queue.CanStart, "Dependência não aprovada liberou a tarefa.");
                queue.SelectedTask = queue.Tasks[0]; await Until(() => queue.CanStart);
                var execution = queue.StartAsync(); await Until(() => vm.HasPermission && queue.CancelCommand.CanExecute(null));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                Capture(window, Path.Combine(output, "01-queue-permission-minimum.png"));
                Require(!queue.StartCommand.CanExecute(null), "Clique duplicado habilitado durante execução.");
                vm.AllowPermissionCommand.Execute(null); await execution;
                Require(queue.CanReview && queue.SelectedTask!.Record.State == WorkspaceTaskState.AwaitingReview, "Término aprovou automaticamente.");
                Require(queue.SelectedAttempt!.Run.TokenUsage?.TotalTokens == 120 && queue.HistoricalTokenUsage.Contains("parcial"), "Primeira tentativa perdeu tokens ou escopo.");
                vm.SelectedConversation = queue.Session; vm.Prompt = "desviar pelo chat";
                Require(!vm.CanSend && !vm.CanEditFunction, "Chat permite alterar tarefa da fila.");
                queue.ReviewNote = "Incluir instruções acessíveis e conferir o envio."; await queue.ReviewAsync(false);
                Require(queue.CanStart, "Ajuste não voltou para início explícito.");
                execution = queue.StartAsync(); await Until(() => vm.HasPermission);
                vm.AllowPermissionCommand.Execute(null); await execution;
                Require(codex.Requests.Count == 2 && codex.Requests[1].NativeSessionId == codex.Requests[0].NativeSessionId,
                    "Ajuste perdeu a sessão nativa.");
                Require(queue.Attempts.Count == 2, "Histórico perdeu a primeira tentativa.");
                queue.SelectedAttempt = queue.Attempts[0]; Require(!queue.CanReview, "Tentativa antiga pode ser aprovada.");
                Require(queue.HistoricalTokenUsage.Contains("120 tokens"), "Seleção antiga mostrou consumo de outra tentativa.");
                queue.SelectedAttempt = queue.Attempts[1]; queue.ReviewNote = "Critérios conferidos pelo usuário.";
                Require(queue.HistoricalTokenUsage.Contains("240 tokens"), "Ajuste somou ou perdeu tokens do run atual.");
                ((System.Windows.Controls.TabControl)window.FindName("TaskTabs")).SelectedIndex = 1;
                Capture(window, Path.Combine(output, "02-queue-review-minimum.png"));
                await queue.ReviewAsync(true);
                queue.SelectedTask = queue.Tasks.Single(t => t.Record.Id == second.Id); await Until(() => queue.CanStart);
                await queue.StartAsync(); Require(queue.CanReview && claude.Requests.Count == 1, "Tarefa dependente não concluiu.");
                Require(queue.HistoricalTokenUsage.Contains("Uso informado do turno principal") && queue.SelectedAttempt!.Run.TokenUsage?.TotalTokens == 120, "Uso Claude não ficou distinto da observação parcial.");
                Require(claude.Requests[0].Prompt.Contains(first.LastRunId ?? "approvedDependencies"), "Contexto da dependência ausente.");
                await queue.ReviewAsync(true);
                queue.SelectedTask = queue.Tasks.Single(t => t.Record.Id == third.Id); await Until(() => queue.CanStart);
                execution = queue.StartAsync(); await Until(() => queue.CancelCommand.CanExecute(null));
                var otherPath = Path.Combine(output, "Outro projeto"); Directory.CreateDirectory(otherPath);
                await vm.AddProjectAsync(otherPath); await Until(() => vm.Project?.Id != project.Id && vm.SelectedConversation is null);
                Require((await vm.LoadTaskQueueAsync()).Tasks.Count == 0, "Fila misturou projetos.");
                queue.CancelCommand.Execute(null); Require(queue.Busy, "Cancelamento liberou antes do encerramento."); await execution;
                Require(queue.SelectedTask!.Record.State == WorkspaceTaskState.Cancelled, "Cancelamento não foi salvo.");
                var reopenedStore = new SqliteWorkspaceStore(database); await reopenedStore.InitializeAsync();
                var reopened = new TaskQueueViewModel(reopenedStore, vm, project); await reopened.InitializeAsync();
                Require(reopened.Tasks.Count == 3 && reopened.Tasks[0].Record.State == WorkspaceTaskState.Approved
                    && reopened.Tasks[0].Record.Attempts == 2, "Reabertura perdeu entregas ou tentativas.");
                await Until(() => reopened.Attempts.Count == 2); reopened.SelectedAttempt = reopened.Attempts[0];
                Require(reopened.HistoricalTokenUsage.Contains("120 tokens"), "Reabertura perdeu consumo da primeira tentativa.");
                reopened.SelectedAttempt = reopened.Attempts[1]; Require(reopened.HistoricalTokenUsage.Contains("240 tokens"), "Reabertura acumulou tentativas indevidamente.");
                Require((await store.EnqueueProposalAsync(project.Id, proposal.Id, proposal.Revision)).Id == queue.SelectedBatch!.Id,
                    "Encaminhamento duplicou o plano.");
                queue.SelectedTask = queue.Tasks.Single(t => t.Record.Id == third.Id); await Until(() => queue.CanStart);
                execution = queue.StartAsync(); await Until(() => queue.CancelCommand.CanExecute(null));
                await vm.StopAsync(); await execution;
                Require(queue.SelectedTask!.Record.State == WorkspaceTaskState.Cancelled && vm.ActiveCount == 0,
                    "Encerramento deixou execução ativa.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                window.Close(); window = null; main.Close();
                Console.WriteLine("PASS: fila geral, encaminhamento sem execução, permissões, ajustes, aprovação de dependências, histórico, cancelamento, dois projetos e reabertura; zero erros de binding. Provedores de teste, nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                await vm.StopAsync();
                if (window is not null && !window.ViewModel.Busy) window.Close();
                if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task<WorkspaceProposal> SeedAsync(SqliteWorkspaceStore store, WorkspaceProject project)
    {
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "Planejamento de teste", ProviderKind.Codex, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "Portal de atendimento", "Receber solicitações por um formulário acessível.",
            [new("form", "Criar formulário de solicitação", "Desenvolvimento", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite,
                "Criar rótulos e validação do formulário.", ["src/"], [], ["Envio acessível pelo teclado.", "Erros de validação legíveis."]),
             new("review", "Revisar formulário", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly,
                "Conferir os critérios e relatar problemas.", ["src/"], ["form"], ["Critérios conferidos."]),
             new("report", "Preparar relatório", "Documentação", ProviderKind.Codex, null, ConversationAccess.ReadOnly,
                "WAIT: preparar relatório.", ["docs/"], [], ["Relatório verificável."])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "Planejar", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, Response = "SIMULAÇÃO: plano de teste.\n```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```",
            FinishedAt = DateTimeOffset.UtcNow }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id);
        return await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado da fila não chegou."); await Task.Delay(25); }
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
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            var native = request.NativeSessionId ?? Guid.NewGuid().ToString(); request = request with { NativeSessionId = native }; Requests.Add(request);
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão de tarefa", native, "modelo-teste"));
            var usage = new RunTokenUsage(kind, Requests.Count * 100, Requests.Count * 20, Requests.Count * 120, Requests.Count * 10, 0);
            progress.Report(new(ConversationEventKind.TokenUsage, "SIMULAÇÃO: tokens", TokenUsage: usage));
            if (request.Prompt.Contains("WAIT"))
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { await Task.Delay(80, CancellationToken.None); }
            }
            if (request.Access == ConversationAccess.WorkspaceWrite)
            {
                var permission = new ConversationPermission(kind, "Write", "SIMULAÇÃO: criar src/formulario.txt. Nenhum arquivo será alterado.", request.WorkingDirectory);
                if (request.PermissionHandler is null || !await request.PermissionHandler(permission, token))
                    return new(native, "modelo-teste", "SIMULAÇÃO: permissão recusada", ConversationOutcome.Blocked, ["Write"]);
            }
            var text = "SIMULAÇÃO: entrega para revisão. Formulário com rótulos acessíveis e validação de campos.\n"
                + "Validação de teste: navegação por teclado e mensagens de erro conferidas pelo provedor simulado. Nenhum arquivo foi modificado.";
            progress.Report(new(ConversationEventKind.TextDelta, text)); await Task.Delay(50, token);
            return new(native, "modelo-teste", text, ConversationOutcome.Completed, [], usage with { IsPartial = kind == ProviderKind.Codex });
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
