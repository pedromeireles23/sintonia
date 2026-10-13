using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

/// <summary>Plan-limit card with simulated usage readers; no CLI, account or model is queried.</summary>
internal static class SubscriptionLimitsSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(output);
        var database = Path.Combine(output, "test-" + Guid.NewGuid() + ".db");
        var projectPath = Path.Combine(output, "Projeto de limites");
        Directory.CreateDirectory(projectPath);
        var listener = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var codex = new Reader(ProviderKind.Codex, call => call switch
        {
            1 => Snapshot(ProviderKind.Codex, 82, TimeSpan.FromMinutes(88), 5, TimeSpan.FromDays(6.35), true),
            2 => Snapshot(ProviderKind.Codex, 0, TimeSpan.FromHours(4.9), 16, TimeSpan.FromDays(6.8), true),
            _ => Snapshot(ProviderKind.Codex, 100, TimeSpan.FromMinutes(20), 17, TimeSpan.FromDays(6.3), false)
        });
        var claude = new Reader(ProviderKind.Claude, call => call == 1
            ? throw new ProviderException("SIMULAÇÃO: o Claude Code instalado não respondeu à consulta de limites.")
            : Snapshot(ProviderKind.Claude, 10, TimeSpan.FromHours(4.7), 7, TimeSpan.FromDays(6.3), null), waitOnCall: 3);
        var store = new SqliteWorkspaceStore(database);
        var vm = new WorkspaceViewModel(store, new(store, []), app.Dispatcher, () => projectPath,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])), usageReaders: [codex, claude]);
        var window = new WorkspaceWindow(vm);
        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready);
                await vm.AddProjectAsync(projectPath);
                var limits = vm.Limits; var codexRow = limits.Providers[0]; var claudeRow = limits.Providers[1];
                await Until(() => codexRow.Status.StartsWith("às ", StringComparison.Ordinal) && claudeRow.Status == "Indisponível");
                Require(limits.HasProviders && codexRow.Provider == ProviderKind.Codex && claudeRow.Provider == ProviderKind.Claude, "Provedores do cartão incorretos.");
                Require(codexRow.Session is { RemainingText: "18%", IsLow: true } && codexRow.Session.ResetText.StartsWith("renova em 1h2", StringComparison.Ordinal),
                    "Janela de 5 horas do Codex incorreta: " + codexRow.Session.ResetText);
                Require(codexRow.Weekly is { RemainingText: "95%", IsLow: false } && codexRow.Weekly.ResetText.StartsWith("renova em 6d", StringComparison.Ordinal),
                    "Janela semanal do Codex incorreta: " + codexRow.Weekly.ResetText);
                Require(!claudeRow.Session.HasData && claudeRow.Session.RemainingText == "—" && claudeRow.StatusDetail.Contains("SIMULAÇÃO"), "Falha do Claude não ficou explícita.");
                Capture(window, Path.Combine(output, "01-limits.png"));

                limits.RefreshCommand.Execute(null);
                await Until(() => claudeRow.Session.HasData && codex.Calls == 2 && !codexRow.Status.StartsWith("Atualizando", StringComparison.Ordinal));
                Require(claudeRow.Session is { RemainingText: "90%", IsLow: false } && claudeRow.Weekly.RemainingText == "93%" && claudeRow.Status.StartsWith("às ", StringComparison.Ordinal),
                    "Atualização do Claude não apareceu.");
                Require(codexRow.Session is { RemainingText: "100%", IsLow: false } && codexRow.Weekly.RemainingText == "84%" && !codexRow.Blocked,
                    "Atualização do Codex não apareceu.");
                vm.Limits.RequestRefresh(ProviderKind.Codex);
                Require(codex.Calls == 2, "Atualização após resposta ignorou o intervalo mínimo.");
                Capture(window, Path.Combine(output, "02-refreshed.png"));

                limits.RefreshCommand.Execute(null);
                await Until(() => claude.Waiting && claudeRow.Status == "Atualizando…" && codex.Calls == 3 && codexRow.Status == "Uso bloqueado");
                Require(codexRow.Session is { RemainingText: "0%", IsLow: true } && codexRow.StatusDetail.Contains("interrompidos"), "Bloqueio informado pelo Codex não ficou visível.");
                window.Width = window.MinWidth; window.Height = window.MinHeight; window.UpdateLayout();
                Capture(window, Path.Combine(output, "03-minimum-blocked.png"));
                var stop = vm.StopAsync();
                if (await Task.WhenAny(stop, Task.Delay(5000)) != stop) throw new TimeoutException("Fechar não cancelou a consulta de limites.");
                Require(claude.Cancelled && claudeRow.Session.RemainingText == "90%", "Cancelamento perdeu os dados anteriores ou não chegou ao leitor.");
                Require(listener.Errors.Count == 0, "Erros de binding: " + string.Join("\n", listener.Errors));
                Console.WriteLine("PASS: cartão de limites com 5 horas/semanal do Codex e do Claude, alerta de pouco restante, falha explícita, atualização, bloqueio do Codex, intervalo mínimo após resposta e cancelamento ao fechar; zero erros de binding. Leitores simulados; nenhuma consulta real.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { if (window.IsVisible) window.Close(); app.Shutdown(exitCode); }
        };
        app.Run(window);
        return exitCode;
    }

    private static ProviderUsageSnapshot Snapshot(ProviderKind provider, int sessionUsed, TimeSpan sessionReset, int weeklyUsed, TimeSpan weeklyReset, bool? allowed) =>
        new(provider, DateTimeOffset.UtcNow, [new(provider == ProviderKind.Codex ? "codex" : "claude", "SIMULAÇÃO",
            new(sessionUsed, 300, DateTimeOffset.UtcNow + sessionReset), new(weeklyUsed, 10080, DateTimeOffset.UtcNow + weeklyReset))], allowed);

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado esperado do cartão de limites não chegou."); await Task.Delay(30); }
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
    private sealed class Reader(ProviderKind kind, Func<int, ProviderUsageSnapshot> answer, int waitOnCall = 0) : IProviderUsageReader
    {
        public ProviderKind Kind => kind;
        public int Calls { get; private set; }
        public bool Waiting { get; private set; }
        public bool Cancelled { get; private set; }
        public async Task<ProviderUsageSnapshot> ReadUsageAsync(string directory, CancellationToken token)
        {
            var call = ++Calls;
            await Task.Delay(40, token);
            if (call == waitOnCall)
            {
                Waiting = true;
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                catch (OperationCanceledException) { Cancelled = true; throw; }
            }
            return answer(call);
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
