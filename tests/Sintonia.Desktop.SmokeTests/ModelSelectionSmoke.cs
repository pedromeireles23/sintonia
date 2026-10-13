using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

/// <summary>Model/reasoning picker with simulated catalogs and providers; no CLI or model is called.</summary>
internal static class ModelSelectionSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(output);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var projectPath = Path.Combine(output, "Projeto de modelos");
        Directory.CreateDirectory(projectPath);
        var listener = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var codex = new FixtureProvider(ProviderKind.Codex, "gpt-6.1-sol-simulado");
        var claude = new FixtureProvider(ProviderKind.Claude, "claude-versao-simulada");
        var store = new SqliteWorkspaceStore(database);
        var inspections = new Dictionary<ProviderKind, int> { [ProviderKind.Codex] = 0, [ProviderKind.Claude] = 0 };
        var claudeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failCodex = false;
        async Task<ProviderCapabilities> Inspect(ProviderKind kind, string directory, CancellationToken token)
        {
            inspections[kind]++;
            if (kind == ProviderKind.Claude) await claudeGate.Task.WaitAsync(token);
            if (kind == ProviderKind.Codex && failCodex) throw new ProviderException("SIMULAÇÃO: consulta Codex indisponível.");
            return kind == ProviderKind.Codex ? CodexCatalog : ClaudeCatalog;
        }
        WorkspaceViewModel MakeViewModel() => new(store, new(store, [codex, claude]), app.Dispatcher, () => projectPath, Inspect);
        var vm = MakeViewModel();
        var window = new WorkspaceWindow(vm);
        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            WorkspaceWindow? reopened = null;
            try
            {
                await Until(() => vm.Ready);
                await vm.AddProjectAsync(projectPath);
                await Until(() => vm.CanConfigure && vm.InstallationModelText.Contains("gpt-6.1-sol"));
                Require(vm.InstallationModelText == "GPT-6.1 Sol · gpt-6.1-sol" && vm.InstallationModelDetail.Contains("Muito alto · xhigh")
                    && vm.InstallationModelDetail.Contains("Configuração do usuário"), "Padrão da instalação Codex não foi exibido: " + vm.InstallationModelDetail);
                Require(vm.Models.Select(m => m.Key).SequenceEqual(["Padrão da instalação", "gpt-6.1-sol", "gpt-6.1-mini"])
                    && vm.Models[1].Detail.Contains("padrão da instalação") && vm.Models[2].Detail.Contains("padrão do catálogo"), "Catálogo Codex incompleto.");
                Require(vm.ReasoningOptions.Select(r => r.Id).SequenceEqual([null, "low", "medium", "high", "xhigh"])
                    && vm.ReasoningOptions[0].Name == "Padrão · xhigh" && vm.CanChooseReasoning, "Reasoning do padrão não foi oferecido.");
                Capture(window, Path.Combine(output, "01-codex-catalog.png"));
                await CaptureDropdownAsync(window, "Model", Path.Combine(output, "01b-model-list.png"));
                await CaptureDropdownAsync(window, "Reasoning", Path.Combine(output, "01c-reasoning-list.png"));

                vm.Model = "gpt-6.1-mini";
                Require(vm.ReasoningOptions.Select(r => r.Id).SequenceEqual([null, "low", "medium"]) && vm.Reasoning.Id is null, "Níveis do modelo escolhido incorretos.");
                vm.Reasoning = vm.ReasoningOptions.Single(r => r.Id == "medium");
                vm.Prompt = "Primeiro pedido (SIMULAÇÃO)."; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0 && vm.SelectedConversation?.State == "Concluída");
                var first = vm.SelectedConversation!;
                Require(codex.Requests[0] is { Model: "gpt-6.1-mini", ReasoningEffort: "medium" }, "Escolha não chegou ao provedor.");
                Require(vm.Model == "gpt-6.1-mini" && first.Record.SelectedModel == "gpt-6.1-mini" && first.Record.Model == "gpt-6.1-sol-simulado",
                    "Versão confirmada substituiu a escolha do usuário.");
                Require(vm.ConversationModelText == "gpt-6.1-sol-simulado" && vm.ConversationModelDetail == "Reasoning Médio · medium, confirmado na última sessão."
                    && first.Description.Contains("medium"), "Modelo em uso não exibido: " + vm.ConversationModelDetail);

                vm.Model = "Padrão da instalação"; vm.Reasoning = vm.ReasoningOptions.Single(r => r.Id == "xhigh");
                Require(vm.ConversationModelDetail.EndsWith("Nova escolha vale no próximo envio.", StringComparison.Ordinal), "Mudança pendente não foi sinalizada.");
                vm.Prompt = "Continuar com o padrão (SIMULAÇÃO)."; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                Require(codex.Requests[1] is { Model: null, ReasoningEffort: "xhigh" } && codex.Requests[1].NativeSessionId == first.Record.NativeSessionId,
                    "Troca entre turnos não retomou a mesma sessão com a nova escolha.");
                Require(first.Record.UsesInstallationDefault == true && !vm.ConversationModelDetail.Contains("Nova escolha"), "Troca entre turnos não foi salva.");
                Capture(window, Path.Combine(output, "02-codex-conversation.png"));
                Require(inspections[ProviderKind.Codex] == 1, "Catálogo Codex consultado repetidamente sem pedido.");

                vm.NewConversationCommand.Execute(null); vm.Provider = ProviderKind.Claude;
                await Until(() => vm.InstallationModelText == "Consultando a instalação…" && !vm.RefreshModelsCommand.CanExecute(null));
                claudeGate.SetResult();
                await Until(() => vm.InstallationModelText == "Padrão (recomendado) · SIMULAÇÃO");
                Require(!vm.CanChooseReasoning && vm.ReasoningHint.Contains("explícito"), "Claude permitiu reasoning sem modelo explícito.");
                vm.Model = "opus";
                Require(vm.CanChooseReasoning && vm.ReasoningOptions.Any(r => r.Id == "max"), "Níveis Claude do modelo não foram oferecidos.");
                vm.Reasoning = vm.ReasoningOptions.Single(r => r.Id == "high");
                vm.Prompt = "segurar"; vm.SendCommand.Execute(null);
                await Until(() => vm.SelectedConversation?.Running == true && claude.Holding);
                Require(!vm.CanChooseModel && !vm.CanChooseReasoning && vm.ConversationModelDetail.EndsWith("Executando com esta configuração.", StringComparison.Ordinal), "Seleção editável durante a execução.");
                claude.Release();
                await Until(() => vm.ActiveCount == 0);
                var second = vm.SelectedConversation!;
                Require(claude.Requests[0] is { Model: "opus", ReasoningEffort: "high" } && vm.Model == "opus", "Escolha Claude não chegou ao provedor.");
                Require(vm.ConversationModelText == "claude-versao-simulada" && vm.ConversationModelDetail == "Reasoning Alto · high solicitado; o CLI não confirma o nível.",
                    "Reasoning Claude apresentado como confirmado: " + vm.ConversationModelDetail);
                Capture(window, Path.Combine(output, "03-claude-conversation.png"));

                vm.SelectedConversation = first;
                await Until(() => vm.Provider == ProviderKind.Codex && vm.InstallationModelText.Contains("gpt-6.1-sol"));
                failCodex = true; vm.RefreshModelsCommand.Execute(null);
                await Until(() => vm.InstallationModelText == "Consulta indisponível" && vm.Notice.Contains("SIMULAÇÃO: consulta Codex indisponível"));
                Require(vm.Models.Count == 3 && vm.Model == "Padrão da instalação" && vm.Reasoning.Id == "xhigh", "Falha da consulta apagou a escolha ou o catálogo anterior.");
                failCodex = false; vm.RefreshModelsCommand.Execute(null);
                await Until(() => vm.InstallationModelText == "GPT-6.1 Sol · gpt-6.1-sol" && inspections[ProviderKind.Codex] == 3);
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Capture(window, Path.Combine(output, "04-minimum.png"));
                Require(codex.Requests.Count == 2 && claude.Requests.Count == 1, "Consulta de catálogo enviou mensagem ao provedor.");

                await vm.StopAsync(); window.Close();
                await Until(() => !window.IsVisible);
                var restored = MakeViewModel(); reopened = new WorkspaceWindow(restored); reopened.Show();
                await Until(() => restored.Ready && restored.Conversations.Count == 2 && restored.SelectedConversation is not null);
                restored.SelectedConversation = restored.Conversations.Single(c => c.Record.Id == second.Record.Id);
                await Until(() => restored.InstallationModelText.StartsWith("Padrão (recomendado)", StringComparison.Ordinal));
                Require(restored.Model == "opus" && restored.Reasoning.Id == "high", "Reabertura perdeu a escolha Claude.");
                restored.SelectedConversation = restored.Conversations.Single(c => c.Record.Id == first.Record.Id);
                await Until(() => restored.InstallationModelText.Contains("gpt-6.1-sol"));
                Require(restored.Model == "Padrão da instalação" && restored.Reasoning.Id == "xhigh"
                    && restored.ConversationModelText == "gpt-6.1-sol-simulado" && restored.ConversationModelDetail.StartsWith("Reasoning Muito alto · xhigh, confirmado", StringComparison.Ordinal),
                    "Reabertura perdeu a escolha Codex: " + restored.ConversationModelDetail);
                Capture(reopened, Path.Combine(output, "05-reopened.png"));
                Require(listener.Errors.Count == 0, "Erros de binding: " + string.Join("\n", listener.Errors));
                await restored.StopAsync(); reopened.Close();
                await Until(() => !reopened.IsVisible);
                Console.WriteLine("PASS: catálogo e padrão da instalação, escolha de modelo/reasoning, troca entre turnos, Claude sem reasoning implícito, bloqueio durante execução, falha de consulta e reabertura; zero erros de binding. Catálogos e provedores simulados; nenhuma chamada real.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                if (window.IsVisible) window.Close();
                if (reopened?.IsVisible == true) reopened.Close();
                app.Shutdown(exitCode);
            }
        };
        app.Run(window);
        return exitCode;
    }

    private static readonly ProviderCapabilities CodexCatalog = new(ProviderKind.Codex,
    [
        new("gpt-6.1-sol", "GPT-6.1 Sol", false, "SIMULAÇÃO: modelo de fronteira para código.", "medium",
            [new("low", "Mais rápido"), new("medium", "Equilibrado"), new("high", "Mais profundo"), new("xhigh", "Máxima profundidade")]),
        new("gpt-6.1-mini", "GPT-6.1 Mini", true, "SIMULAÇÃO: respostas rápidas.", "low", [new("low", "Mais rápido"), new("medium", "Equilibrado")])
    ], [], [], new("gpt-6.1-sol", "xhigh", "Configuração do usuário", "Configuração do usuário"));

    private static readonly ProviderCapabilities ClaudeCatalog = new(ProviderKind.Claude,
    [
        new("default", "Padrão (recomendado) · SIMULAÇÃO", false, "SIMULAÇÃO: modelo escolhido pelo Claude Code.", null, []),
        new("opus", "Opus · SIMULAÇÃO", false, "SIMULAÇÃO: tarefas complexas.", null, [new("low", ""), new("medium", ""), new("high", ""), new("max", "")]),
        new("sonnet", "Sonnet · SIMULAÇÃO", false, "", null, [new("low", ""), new("high", "")])
    ], [], ["SIMULAÇÃO: o CLI não informa o padrão nesta consulta."]);

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado esperado da central não chegou."); await Task.Delay(30); }
    }
    private static async Task CaptureDropdownAsync(Window window, string id, string path)
    {
        var picker = Find<ComboBox>(window, id);
        picker.IsDropDownOpen = true;
        await Task.Delay(150);
        var list = (FrameworkElement)((Popup)picker.Template.FindName("PART_Popup", picker)).Child;
        list.UpdateLayout();
        var image = new RenderTargetBitmap((int)list.ActualWidth, (int)list.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(list);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var stream = File.Create(path)) encoder.Save(stream);
        picker.IsDropDownOpen = false;
        await Task.Delay(50);
    }
    private static T Find<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        if (root is T match && AutomationProperties.GetAutomationId(match) == id) return match;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindOrNull<T>(VisualTreeHelper.GetChild(root, index), id) is { } found) return found;
        throw new KeyNotFoundException(id);
    }
    private static T? FindOrNull<T>(DependencyObject root, string id) where T : FrameworkElement
    {
        try { return Find<T>(root, id); } catch (KeyNotFoundException) { return null; }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var surface = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class FixtureProvider(ProviderKind kind, string version) : IConversationProvider
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public bool Holding { get; private set; }
        public void Release() => _release.TrySetResult();
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
            // Codex confirms the effective effort; Claude does not, so it stays unconfirmed.
            var effort = kind == ProviderKind.Codex ? request.ReasoningEffort ?? "xhigh" : null;
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão de teste.", id, version, ReasoningEffort: effort));
            if (request.Prompt == "segurar") { Holding = true; await _release.Task.WaitAsync(cancellationToken); Holding = false; }
            else await Task.Delay(60, cancellationToken);
            return new(id, version, "SIMULAÇÃO: resposta de teste.", ConversationOutcome.Completed, [], ReasoningEffort: effort);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
