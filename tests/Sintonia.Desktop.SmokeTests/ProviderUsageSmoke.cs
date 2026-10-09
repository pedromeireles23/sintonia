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

internal static class ProviderUsageSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var root = Path.Combine(output, "run-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        var firstDirectory = Path.Combine(root, "Pesquisa de referências ação"); Directory.CreateDirectory(firstDirectory);
        var otherDirectory = Path.Combine(root, "Outro projeto"); Directory.CreateDirectory(otherDirectory);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var store = new SqliteWorkspaceStore(Path.Combine(root, "workspace.db")); var worker = new Worker();
        var vm = new WorkspaceViewModel(store, new(store, [worker]), app.Dispatcher, () => firstDirectory,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var codex = new Reader(ProviderKind.Codex); var claude = new Reader(ProviderKind.Claude);
        var main = new WorkspaceWindow(vm, [codex, claude]); var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready); await vm.AddProjectAsync(firstDirectory); await Until(() => vm.CanConfigure);
                var project = vm.Project!;
                Button(main, "ProviderUsage").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var window = app.Windows.OfType<ProviderUsageWindow>().Single(); var usage = window.ViewModel;
                await Until(() => !usage.Busy && usage.Snapshot is not null);
                Require(codex.Calls == 1 && worker.Calls == 0, "Consulta iniciou modelo ou não usou o leitor de teste.");
                Require(usage.Snapshot!.OrdinaryUsageAllowed == true && usage.Buckets.Single().Windows[0].Percentages.Contains("75% restante"), "Uso tipado não chegou ao painel.");
                Require(usage.Buckets.Single().Windows[1].Percentages == "Percentuais indisponíveis", "Janela ausente virou zero.");
                Capture(window, Path.Combine(output, "01-usage-normal.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Require(Button(window, "RefreshProviderUsage").IsVisible && Button(window, "CancelProviderUsage").IsVisible, "Ações ausentes no tamanho mínimo.");
                Capture(window, Path.Combine(output, "02-usage-minimum.png"));
                main.Width = main.MinWidth; main.Height = main.MinHeight;
                await app.Dispatcher.InvokeAsync(main.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                var navigation = Button(main, "ProviderUsage"); var position = navigation.TranslatePoint(new Point(), main);
                Require(position.Y >= 0 && position.Y + navigation.ActualHeight < main.ActualHeight, "Ação de uso saiu da central mínima.");
                Capture(main, Path.Combine(output, "03-workspace-minimum.png"));

                usage.Provider = ProviderKind.Claude;
                Require(usage.Snapshot is null && usage.Buckets.Count == 0, "Troca de provedor conservou dados anteriores.");
                await usage.RefreshAsync();
                Require(usage.Snapshot is { HasData: false, OrdinaryUsageAllowed: null } && usage.Notice.Contains("SIMULAÇÃO") && claude.Calls == 1, "Ausência de quota Claude não ficou explícita.");
                Capture(window, Path.Combine(output, "04-claude-unavailable.png"));
                usage.Provider = ProviderKind.Codex; codex.Result = () => Snapshot(false); await usage.RefreshAsync();
                Require(usage.Allowance.Contains("indisponível") && usage.Snapshot!.OrdinaryUsageAllowed == false, "Recusa explícita não apareceu.");
                codex.Result = () => Snapshot(null); await usage.RefreshAsync();
                Require(usage.Allowance.Contains("não informada"), "Ausência de decisão virou autorização.");
                codex.Result = () => throw new IOException("SEGREDO: erro bruto de teste"); await usage.RefreshAsync();
                Require(usage.Snapshot is null && usage.Buckets.Count == 0 && !usage.Notice.Contains("SEGREDO"), "Falha conservou dados antigos ou revelou erro bruto.");
                codex.Result = () => Snapshot(true); codex.Hold(); var pending = usage.RefreshAsync(); await codex.Started!.Task;
                Require(usage.Busy && !usage.CanConfigure && !usage.RefreshCommand.CanExecute(null), "Consulta permitiu início concorrente.");
                usage.Provider = ProviderKind.Claude; Require(usage.Provider == ProviderKind.Codex, "Provedor mudou durante consulta.");
                await vm.AddProjectAsync(otherDirectory); await Until(() => vm.CanConfigure);
                Require(usage.Project.Id == project.Id && codex.LastDirectory == project.Directory, "Painel perdeu a pasta de origem ao navegar.");
                usage.CancelCommand.Execute(null);
                Require(codex.LastToken.IsCancellationRequested && usage.Busy && usage.Snapshot is null, "Cancelar liberou controles antes do encerramento.");
                codex.Release!.SetResult(); await pending;
                Require(!usage.Busy && usage.Snapshot is null && usage.Notice.Contains("cancelada"), "Resposta tardia cancelada virou dados atuais.");

                codex.Hold(); pending = usage.RefreshAsync(); await codex.Started!.Task; window.Close();
                await Until(() => codex.LastToken.IsCancellationRequested);
                Require(window.IsVisible && !window.IsEnabled && usage.Busy, "Fechamento não aguardou a consulta.");
                codex.Release!.SetResult(); await pending; await Until(() => !window.IsVisible);
                Button(main, "ProviderUsage").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window = app.Windows.OfType<ProviderUsageWindow>().Single(); usage = window.ViewModel;
                await Until(() => !usage.Busy && usage.Snapshot is not null);
                Require(usage.Project.Id == vm.Project!.Id && !ReferenceEquals(usage.Project, project), "Reabertura reutilizou o projeto anterior.");
                codex.Hold(); pending = usage.RefreshAsync(); await codex.Started!.Task; main.Close();
                await Until(() => codex.LastToken.IsCancellationRequested);
                Require(main.IsVisible && window.IsVisible && !main.IsEnabled, "Central fechou antes da consulta do painel.");
                codex.Release!.SetResult(); await pending; await Until(() => !main.IsVisible && !window.IsVisible);
                Require(worker.Calls == 0 && errors.Errors.Count == 0, "Modelo iniciado ou erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: uso WPF com dados de teste, conta/horário/janelas, ausência Claude, indisponibilidade explícita/nulos/falha, troca de provedor, navegação, consulta exclusiva, cancelamento/tardio, reabertura e fechamento aguardado do painel/central; tamanhos normal/mínimo e zero erros de binding. Nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally
            {
                codex.Release?.TrySetResult(); claude.Release?.TrySetResult();
                foreach (var window in app.Windows.OfType<ProviderUsageWindow>().ToArray()) { await window.ViewModel.StopAsync(); window.Close(); }
                await vm.StopAsync(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static ProviderUsageSnapshot Snapshot(bool? allowed) => new(ProviderKind.Codex, DateTimeOffset.UtcNow,
        [new("codex", "Codex · DADOS DE TESTE", new(25, 300, DateTimeOffset.Parse("2030-01-01T00:00:00Z")), null)], allowed);
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class Reader(ProviderKind kind) : IProviderUsageReader
    {
        public ProviderKind Kind => kind;
        public Func<ProviderUsageSnapshot> Result { get; set; } = kind == ProviderKind.Codex ? () => Snapshot(true)
            : () => ProviderUsageSnapshot.Unavailable(ProviderKind.Claude, "SIMULAÇÃO: quota da assinatura indisponível nesta integração. Consulte /usage no Claude Code ou as configurações de uso em claude.ai.");
        public int Calls { get; private set; }
        public string? LastDirectory { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public TaskCompletionSource? Started { get; private set; }
        public TaskCompletionSource? Release { get; private set; }
        public void Hold() { Started = new(TaskCreationOptions.RunContinuationsAsynchronously); Release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public async Task<ProviderUsageSnapshot> ReadUsageAsync(string directory, CancellationToken token)
        {
            Calls++; LastDirectory = directory; LastToken = token;
            if (Release is not null) { Started!.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
            return Result(); // Deliberately late after cancellation, to verify the panel discards it.
        }
    }
    private sealed class Worker : IConversationProvider
    {
        public ProviderKind Kind => ProviderKind.Codex;
        public int Calls { get; private set; }
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Este teste não deve chamar modelos."); }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
