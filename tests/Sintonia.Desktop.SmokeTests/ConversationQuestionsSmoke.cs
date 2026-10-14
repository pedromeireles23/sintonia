using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Desktop.SmokeTests;

internal static class ConversationQuestionsSmoke
{
    private static readonly string Input = JsonSerializer.Serialize(new { questions = new object[]
    {
        new { question = "Qual formato usar no relatório de teste?", header = "Formato", multiSelect = false, options = new[]
            { new { label = "Resumo", description = "Apresenta apenas os resultados principais." }, new { label = "Detalhes", description = "Inclui explicações e exemplos." } } },
        new { question = "Quais etapas devem entrar no relatório?", header = "Etapas", multiSelect = true, options = new[]
            { new { label = "Implementação", description = "Registra o que foi implementado." }, new { label = "Revisão", description = "Registra os critérios conferidos." } } }
    } });
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var projectPath = Path.Combine(output, "Projeto de perguntas ação"); Directory.CreateDirectory(projectPath);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var store = new SqliteWorkspaceStore(database);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var provider = new FixtureProvider();
        var vm = new WorkspaceViewModel(store, new(store, [provider]), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [new("fixture-model", "SIMULAÇÃO", true)], [], [])));
        var window = new WorkspaceWindow(vm); TaskQueueWindow? queueWindow = null; var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(projectPath); await WorkspaceSmoke.NewChatAsync(vm);
                void Send() { vm.Provider = ProviderKind.Claude; vm.Prompt = "SIMULAÇÃO de perguntas; nenhum modelo."; vm.SendCommand.Execute(null); }
                Send(); await Until(() => vm.HasQuestions);
                var first = vm.SelectedConversation!; var panel = vm.Questions!;
                Require(!vm.HasPermission && !panel.CanSubmit, "Pergunta virou autorização ou seleção automática.");
                panel.SubmitCommand.Execute(null); Require(vm.Questions == panel && vm.ActiveCount == 1, "Envio incompleto encerrou pergunta.");
                await Until(() => first.Events.Contains("Claude aguarda respostas às perguntas.") && first.Messages.Last().Text.Contains("DURANTE-PERGUNTA"));
                Require(Find<Button>(window, button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "SubmitClaudeQuestions").IsEnabled == false, "Botão sem respostas ficou habilitado.");
                Capture(window, Path.Combine(output, "01-questions.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                Capture(window, Path.Combine(output, "02-questions-minimum.png"));
                VerifyLayout(window);
                panel.Questions[0].Choices[0].IsSelected = true; panel.Questions[0].Choices[1].IsSelected = true;
                Require(!panel.Questions[0].Choices[0].IsSelected && !panel.CanSubmit, "Escolha simples manteve duas ou ignorou pergunta faltante.");
                panel.Questions[1].Choices[0].IsSelected = true; panel.Questions[1].Choices[1].IsSelected = true;
                Require(panel.CanSubmit, "Seleção múltipla não habilitou resposta.");
                var literal = "  Outra resposta com ação \"literal\"\n$(não executar) &| ";
                panel.Questions[0].CustomText = literal;
                Require(panel.Questions[0].Choices.All(choice => !choice.IsSelected) && panel.CanSubmit, "Texto livre não substituiu a escolha.");
                panel.SubmitCommand.Execute(null); await Until(() => vm.ActiveCount == 0 && !vm.HasQuestions);
                Require(first.State == "Concluída" && !panel.SubmitCommand.CanExecute(null), "Resposta ou retirada não concluiu.");
                using (var result = JsonDocument.Parse(first.Messages.Last().Text))
                {
                    Require(result.RootElement.GetProperty(panel.Questions[0].Text).GetString() == literal, "Texto livre foi alterado.");
                    Require(result.RootElement.GetProperty(panel.Questions[1].Text).GetArrayLength() == 2, "Múltiplas escolhas perdidas.");
                }
                var nativeId = first.Record.NativeSessionId;
                Send(); await Until(() => vm.HasQuestions);
                Require(!vm.Questions!.CanSubmit && vm.Questions.Questions.All(question => question.CustomText.Length == 0), "Resposta anterior foi herdada.");
                vm.Questions.DeclineCommand.Execute(null); await Until(() => vm.ActiveCount == 0);
                Require(first.State == "Bloqueada" && provider.Requests[^1].NativeSessionId == nativeId, "Recusa/retomada perdeu estado.");

                Send(); await Until(() => vm.HasQuestions); var waiting = vm.Questions!;
                vm.NewConversationCommand.Execute(null); Send(); var second = vm.SelectedConversation!;
                await Until(() => vm.ActiveCount == 2 && second.Messages.LastOrDefault()?.Text.Contains("DURANTE-PERGUNTA") == true);
                Require(vm.Questions == waiting, "Perguntas paralelas substituíram formulário.");
                vm.CancelCommand.Execute(null); await Until(() => vm.ActiveCount == 1);
                Require(vm.Questions == waiting && waiting.IsActive && second.State == "Cancelada", "Cancelamento em espera retirou outro formulário.");
                vm.SelectedConversation = first; vm.CancelCommand.Execute(null); await Until(() => vm.ActiveCount == 0 && !vm.HasQuestions);
                waiting.SubmitCommand.Execute(null); Require(!waiting.IsActive, "Formulário retirado aceitou resposta tardia.");

                provider.Scenario = "claude-question-withdraw"; Send(); await Until(() => vm.ActiveCount == 0);
                Require(!vm.HasQuestions && first.State == "Concluída", "Retirada do provedor deixou pergunta pendente.");
                vm.Access = vm.AccessOptions.Single(a => a.Value == ConversationAccess.ReadOnly);
                provider.Scenario = "claude-question-write"; Send(); await Until(() => vm.HasQuestions);
                Answer(vm.Questions!); await Until(() => vm.ActiveCount == 0);
                Require(first.State == "Bloqueada" && !vm.HasPermission && !File.Exists(Path.Combine(projectPath, "NÃO-CRIAR.txt")), "Resposta autorizou escrita em leitura.");

                provider.Scenario = "claude-question"; var project = vm.Project!;
                await SeedQueueAsync(store, project);
                var queue = await vm.LoadTaskQueueAsync(); queueWindow = new(queue) { Owner = window }; queueWindow.Show();
                await queue.EnqueueAsync(); await Until(() => queue.CanStart);
                var execution = queue.StartAsync(); await Until(() => vm.HasQuestions);
                queueWindow.Width = queueWindow.MinWidth; queueWindow.Height = queueWindow.MinHeight;
                Capture(queueWindow, Path.Combine(output, "03-queue-questions-minimum.png"));
                VerifyLayout(queueWindow);
                Require(Find<Button>(queueWindow, button => System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "SubmitClaudeQuestions").IsVisible, "Perguntas ausentes na fila.");
                Answer(vm.Questions!); await execution;
                Require(queue.SelectedTask!.Record.State == WorkspaceTaskState.AwaitingReview, "Resposta aprovou entrega da fila automaticamente.");
                queueWindow.Close(); queueWindow = null;

                vm.NewConversationCommand.Execute(null); Send(); await Until(() => vm.HasQuestions);
                var closing = vm.Questions!; await vm.StopAsync();
                Require(vm.ActiveCount == 0 && !vm.HasQuestions && !closing.IsActive, "Encerramento conservou formulário/execução.");
                var reopened = new SqliteWorkspaceStore(database); await reopened.InitializeAsync();
                Require((await reopened.GetRunsAsync(first.Record.Id)).All(run => run.State != ChatRunState.Running), "Reabertura perdeu término das tentativas.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: WPF perguntas simples/múltiplas/texto livre, recusa, retomada, serialização, cancelamento de espera/decisão, retirada nativa, leitura sem escrita, fila/revisão e encerramento; zero erros de binding. Adaptador real com processo de teste; nenhum modelo ou rede.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                await vm.StopAsync(); if (queueWindow is not null && !queueWindow.ViewModel.Busy) queueWindow.Close();
                if (window.IsVisible) window.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(window); return exitCode;
    }

    private static void Answer(ConversationQuestionsViewModel panel)
    {
        foreach (var question in panel.Questions) question.Choices[0].IsSelected = true;
        Require(panel.CanSubmit, "Resposta completa recusada."); panel.SubmitCommand.Execute(null);
    }
    private static void VerifyLayout(Window window)
    {
        var view = Find<ConversationQuestionsView>(window, candidate => candidate.IsVisible);
        var scroll = Find<ScrollViewer>(view, candidate => candidate.IsVisible);
        Require(scroll.ViewportHeight >= 80, "Perguntas ficaram sem espaço para leitura no tamanho mínimo.");
        var button = Find<Button>(view, candidate => System.Windows.Automation.AutomationProperties.GetAutomationId(candidate) == "DeclineClaudeQuestions");
        var surface = (FrameworkElement)window.Content;
        var bottom = button.TransformToAncestor(surface).Transform(new Point(0, button.ActualHeight));
        Require(button.IsVisible && bottom.Y <= surface.ActualHeight && bottom.X >= 0, "Botão de recusa ficou fora da janela.");
    }
    private static async Task SeedQueueAsync(SqliteWorkspaceStore store, WorkspaceProject project)
    {
        var conversation = new WorkspaceConversation(Guid.NewGuid().ToString(), project.Id, "SIMULAÇÃO: proposta", ProviderKind.Claude, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.ReadOnly);
        await store.SaveConversationAsync(conversation);
        var plan = new PlanProposal(1, "SIMULAÇÃO: perguntas", "Responder escolhas do processo de teste.",
            [new("question", "Consultar preferências", "Revisão", ProviderKind.Claude, null, ConversationAccess.ReadOnly,
                "SIMULAÇÃO de perguntas.", ["docs/"], [], ["Escolhas registradas."])]);
        var run = new ChatRun(Guid.NewGuid().ToString(), conversation.Id, "SIMULAÇÃO", null, ChatRunState.Running, DateTimeOffset.UtcNow, null, null);
        await store.BeginRunAsync(run);
        await store.FinishRunAsync(run with { State = ChatRunState.Completed, FinishedAt = DateTimeOffset.UtcNow,
            Response = "```sintonia-plan\n" + PlanProposalFormat.Serialize(plan) + "\n```" }, conversation, []);
        var proposal = await store.CreateProposalAsync(project.Id, run.Id);
        await store.SaveProposalAsync(proposal with { State = ProposalReviewState.Approved });
    }
    private static T Find<T>(DependencyObject parent, Func<T, bool> predicate) where T : DependencyObject
    {
        var result = FindOrNull(parent); return result ?? throw new InvalidOperationException("Controle não encontrado.");
        T? FindOrNull(DependencyObject current)
        {
            if (current is T value && predicate(value)) return value;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(current); index++)
                if (FindOrNull(VisualTreeHelper.GetChild(current, index)) is { } found) return found;
            return null;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado esperado das perguntas não chegou."); await Task.Delay(25); }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (message is not null) Errors.Add(message); }
        public override void WriteLine(string? message) { if (message is not null) Errors.Add(message); }
    }
    private sealed class FixtureProvider : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;
        public string Scenario { get; set; } = "claude-question";
        public List<ConversationRequest> Requests { get; } = [];
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Requests.Add(request);
            var launch = new ExecutableLaunch(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [Scenario], "Teste");
            // The fixture generates predetermined questions. No real model interprets the task prompt.
            return new ClaudeConversationProvider(launch).SendAsync(request with { Prompt = Input }, progress, token);
        }
    }
}
