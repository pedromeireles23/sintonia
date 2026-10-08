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

internal static class WorkspaceSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(output);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var projectA = Path.Combine(output, "Jogo de teste ação");
        var projectB = Path.Combine(output, "Outro projeto");
        Directory.CreateDirectory(projectA); Directory.CreateDirectory(projectB);
        var listener = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var codex = new FixtureProvider(ProviderKind.Codex);
        var claude = new FixtureProvider(ProviderKind.Claude);
        var store = new SqliteWorkspaceStore(database);
        WorkspaceViewModel MakeViewModel() => new(store, new(store, [codex, claude]), app.Dispatcher, () => projectA,
            (provider, _, _) => Task.FromResult(new ProviderCapabilities(provider, [new("modelo-teste", "Modelo de teste", true)], [], [])));
        var vm = MakeViewModel();
        var window = new WorkspaceWindow(vm);
        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            WorkspaceWindow? reopened = null;
            try
            {
                await Until(() => vm.Ready);
                Capture(window, Path.Combine(output, "01-empty.png"));
                await vm.AddProjectAsync(projectA);
                await Until(() => vm.CanConfigure);
                vm.Prompt = "Leia e confira o projeto (teste com provedor simulado).";
                vm.SendCommand.Execute(null);
                await Until(() => vm.HasPermission);
                Require(!vm.CanSend && vm.ActiveCount == 1, "Duplicação de envio permitida.");
                Capture(window, Path.Combine(output, "02-permission.png"));
                vm.AllowPermissionCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                var first = vm.SelectedConversation!;
                Require(first.State == "Concluída" && first.Record.NativeSessionId is not null, "Resposta/sessão não foram registradas.");
                var nativeId = first.Record.NativeSessionId;
                vm.Prompt = "Continue a conversa."; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                Require(first.Record.NativeSessionId == nativeId && codex.Requests[^1].NativeSessionId == nativeId, "Retomada não encaminhou identificador nativo.");
                vm.NewConversationCommand.Execute(null);
                vm.Provider = ProviderKind.Claude;
                vm.Prompt = "cancelar"; vm.SendCommand.Execute(null);
                await Until(() => vm.SelectedConversation?.Messages.Count == 2);
                var cancelled = vm.SelectedConversation!;
                vm.CancelCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                Require(cancelled.State == "Cancelada", "Cancelamento não registrado.");
                vm.SelectedConversation = first;
                Capture(window, Path.Combine(output, "03-history.png"));
                vm.Prompt = "paralelo"; vm.SendCommand.Execute(null);
                vm.NewConversationCommand.Execute(null); vm.Provider = ProviderKind.Claude;
                vm.Prompt = "paralelo"; vm.SendCommand.Execute(null);
                Require(vm.ActiveCount == 2, "A central não admitiu as duas IAs em leitura.");
                await Until(() => vm.ActiveCount == 0);
                var original = vm.Project!;
                await vm.AddProjectAsync(projectB);
                await Until(() => vm.CanConfigure && vm.Conversations.Count == 0);
                vm.Provider = ProviderKind.Claude; vm.Prompt = "Outra pasta."; vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0);
                Require(vm.Conversations.Count == 1 && claude.Requests[^1].WorkingDirectory == projectB, "Projeto não foi isolado.");
                vm.Project = original;
                await Until(() => vm.Conversations.Count == 3 && vm.SelectedConversation is not null);
                vm.SelectedConversation = first;
                Require(first.Messages.Count == 6, "Histórico duplicado ou perdido ao navegar.");
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Capture(window, Path.Combine(output, "04-minimum.png"));
                await vm.StopAsync(); window.Close();
                await Until(() => !window.IsVisible);
                var restored = MakeViewModel(); reopened = new WorkspaceWindow(restored); reopened.Show();
                await Until(() => restored.Ready && restored.Conversations.Count == 3);
                restored.SelectedConversation = restored.Conversations.Single(c => c.Record.Id == first.Record.Id);
                await Until(() => restored.SelectedConversation?.Loaded == true);
                Require(restored.SelectedConversation!.Record.NativeSessionId == nativeId && restored.Messages?.Count == 6, "Histórico não sobreviveu à reabertura.");
                Capture(reopened, Path.Combine(output, "05-reopened.png"));
                Require(listener.Errors.Count == 0, "Erros de binding: " + string.Join("\n", listener.Errors));
                await restored.StopAsync(); reopened.Close();
                await Until(() => !reopened.IsVisible);
                Console.WriteLine("PASS: central WPF, dois projetos, autorização, retomada, cancelamento e reabertura SQLite. Provedores de teste; nenhuma chamada real.");
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

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado esperado da central não chegou."); await Task.Delay(30); }
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
    private sealed class FixtureProvider(ProviderKind kind) : IConversationProvider
    {
        public ProviderKind Kind => kind;
        public List<ConversationRequest> Requests { get; } = [];
        public async Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var id = request.NativeSessionId ?? Guid.NewGuid().ToString();
            progress.Report(new(ConversationEventKind.Session, "SIMULAÇÃO: sessão de teste.", id, "modelo-teste"));
            if (kind == ProviderKind.Codex && request.NativeSessionId is null && request.PermissionHandler is not null)
            {
                var approved = await request.PermissionHandler(new(kind, "command", "SIMULAÇÃO: leitura de uma amostra. Nenhum comando será executado.", request.WorkingDirectory), cancellationToken);
                if (!approved) return new(id, "modelo-teste", "Permissão recusada.", ConversationOutcome.Blocked, ["Read"]);
            }
            progress.Report(new(ConversationEventKind.TextDelta, "SIMULAÇÃO: resposta recebida em partes."));
            if (request.Prompt == "cancelar") await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            else await Task.Delay(request.Prompt == "paralelo" ? 500 : 120, cancellationToken);
            return new(id, "modelo-teste", "SIMULAÇÃO DE TESTE. A central salvou a resposta, o modelo e a sessão nativa fictícia. Reabrir esta conversa preserva o histórico; continuar encaminha o mesmo identificador ao provedor de teste.", ConversationOutcome.Completed, []);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
