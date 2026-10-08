using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Desktop.SmokeTests;

/// <summary>Opt-in only: exactly one small reading turn per provider, outside the automatic test suite.</summary>
internal static class WorkspaceRealSmoke
{
    public static int Run()
    {
        var output = Path.GetFullPath("artifacts/workspace-real"); Directory.CreateDirectory(output);
        var folder = Path.Combine(output, "Verificação de projeto ação"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "amostra.txt"), "Itens: 12, 8, 5. Marcador: SINTONIA-CENTRAL-7341.");
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var store = new SqliteWorkspaceStore(Path.Combine(output, "proof-" + Guid.NewGuid() + ".db"));
        var vm = new WorkspaceViewModel(store, new(store, [new CodexConversationProvider(), new ClaudeConversationProvider()]), app.Dispatcher,
            () => folder, (_, _, _) => Task.FromResult(new ProviderCapabilities(ProviderKind.Codex, [], [], [])));
        var window = new WorkspaceWindow(vm);
        var code = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready, 10);
                await vm.AddProjectAsync(folder);
                await Until(() => vm.CanConfigure, 10);
                foreach (var provider in Enum.GetValues<ProviderKind>())
                {
                    vm.NewConversationCommand.Execute(null); vm.Provider = provider;
                    vm.Prompt = "Leia amostra.txt com a ferramenta de leitura e confirme a soma dos itens e o marcador. Responda em português em duas frases. Não altere arquivos nem use outros agentes.";
                    vm.SendCommand.Execute(null);
                    await Until(() => vm.ActiveCount == 0, 150);
                    var session = vm.SelectedConversation!;
                    var runs = await store.GetRunsAsync(session.Record.Id);
                    var run = runs.Single();
                    if (run.State != ChatRunState.Completed || run.Response?.Contains("25") != true || !run.Response.Contains("SINTONIA-CENTRAL-7341"))
                        throw new InvalidOperationException($"{provider}: {run.Error ?? run.Response}");
                    Console.WriteLine($"PASS: central real {provider}, modelo {session.Record.Model}, resposta salva no histórico SQLite.");
                }
                window.UpdateLayout();
                var surface = (FrameworkElement)window.Content;
                var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, "central-real.png"))) encoder.Save(stream);
                code = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { await vm.StopAsync(); window.Close(); await Task.Delay(100); app.Shutdown(code); }
        };
        app.Run(window);
        return code;
    }
    private static async Task Until(Func<bool> condition, int seconds)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(seconds);
        while (!condition()) { if (DateTimeOffset.UtcNow >= deadline) throw new TimeoutException("Prova real da central excedeu o prazo; será cancelada."); await Task.Delay(50); }
    }
}
