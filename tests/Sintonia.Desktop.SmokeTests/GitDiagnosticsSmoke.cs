using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Persistence;

namespace Sintonia.Desktop.SmokeTests;

internal static class GitDiagnosticsSmoke
{
    public static int Run(string outputPath)
    {
        var output = Path.GetFullPath(outputPath); Directory.CreateDirectory(output);
        var temporary = Path.Combine(Path.GetTempPath(), "sintonia-git-ui " + Guid.NewGuid()); Directory.CreateDirectory(temporary);
        var repo = Path.Combine(temporary, "Portal de referências"); var plain = Path.Combine(temporary, "Análise sem Git");
        Directory.CreateDirectory(repo); Directory.CreateDirectory(plain);
        var app = new App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var store = new SqliteWorkspaceStore(Path.Combine(temporary, "workspace.db"));
        var worker = new Worker();
        var vm = new WorkspaceViewModel(store, new(store, [worker]), app.Dispatcher, () => repo,
            (kind, _, _) => Task.FromResult(new ProviderCapabilities(kind, [], [], [])));
        var main = new WorkspaceWindow(vm); var exitCode = 1;
        main.Loaded += async (_, _) =>
        {
            try
            {
                await Until(() => vm.Ready);
                await GitAsync(repo, "init", "--template=", "--initial-branch=main");
                Directory.CreateDirectory(Path.Combine(repo, "docs"));
                await File.WriteAllTextAsync(Path.Combine(repo, "docs", "pesquisa inicial.md"), "Referências do projeto.\n");
                await File.WriteAllTextAsync(Path.Combine(repo, "antigo.txt"), "Exemplo antigo.\n");
                await GitAsync(repo, "add", "-f", "."); await GitAsync(repo, "commit", "-m", "fixture");
                await GitAsync(repo, "mv", "docs/pesquisa inicial.md", "docs/referências.md");
                await File.AppendAllTextAsync(Path.Combine(repo, "docs", "referências.md"), "Revisar as fontes.\n");
                File.Delete(Path.Combine(repo, "antigo.txt")); await File.WriteAllTextAsync(Path.Combine(repo, "rascunho.txt"), "Análise em andamento.\n");
                var index = Path.Combine(repo, ".git", "index"); var before = SHA256.HashData(await File.ReadAllBytesAsync(index));
                var modifiedAt = File.GetLastWriteTimeUtc(index);
                await vm.AddProjectAsync(Path.Combine(repo, "docs")); await Until(() => vm.CanConfigure);
                var selectedProject = vm.Project!;
                Button(main, "GitDiagnostics").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                var window = app.Windows.OfType<GitDiagnosticsWindow>().Single();
                await Until(() => !window.ViewModel.Busy && window.ViewModel.Snapshot is not null);
                Require(window.ViewModel.Snapshot!.State == GitDiagnosticState.Available && window.ViewModel.Branch == "main", "Estado Git real não apareceu.");
                Require(Path.GetFullPath(window.ViewModel.RootDirectory) == Path.GetFullPath(repo), "Subpasta perdeu a raiz Git.");
                Require(window.ViewModel.Changes.Count == 3 && window.ViewModel.Changes.Single(c => c.HasPreviousPath).IndexText == "Renomeado"
                    && window.ViewModel.Changes.Single(c => c.HasPreviousPath).WorktreeText == "Modificado", "Alterações/renomeação não chegaram à janela.");
                Capture(window, Path.Combine(output, "01-git-normal.png"));
                window.Width = window.MinWidth; window.Height = window.MinHeight;
                await app.Dispatcher.InvokeAsync(window.UpdateLayout, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                Capture(window, Path.Combine(output, "02-git-minimum.png"));
                Require(Button(window, "RefreshGitDiagnostics").IsVisible, "Atualizar ficou inacessível no tamanho mínimo.");
                var grid = (DataGrid)window.FindName("ChangedFiles");
                Require(grid.Columns.Sum(c => c.ActualWidth) <= grid.ActualWidth && grid.ActualHeight >= 140,
                    "Tabela Git ficou sem espaço ou perdeu colunas no tamanho mínimo.");
                main.Width = main.MinWidth; main.Height = main.MinHeight; Capture(main, Path.Combine(output, "03-central-minimum.png"));
                Require(worker.Calls == 0, "Diagnóstico chamou um provedor.");
                await vm.AddProjectAsync(plain); await Until(() => vm.CanConfigure);
                await window.ViewModel.RefreshAsync();
                Require(window.ViewModel.Project.Id == selectedProject.Id && window.ViewModel.Snapshot!.State == GitDiagnosticState.Available,
                    "Alternar a central redirecionou a janela de outro projeto.");
                var after = SHA256.HashData(await File.ReadAllBytesAsync(index));
                Require(before.SequenceEqual(after) && modifiedAt == File.GetLastWriteTimeUtc(index), "Diagnóstico modificou o índice.");
                await CloseAsync(window);
                Button(main, "GitDiagnostics").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                window = app.Windows.OfType<GitDiagnosticsWindow>().Single();
                await Until(() => !window.ViewModel.Busy && window.ViewModel.Snapshot is not null);
                Require(window.ViewModel.Snapshot!.State == GitDiagnosticState.NotRepository && window.ViewModel.Changes.Count == 0
                    && window.ViewModel.StatusTitle == "Projeto sem Git", "Pasta comum foi tratada como repositório limpo.");
                vm.Prompt = "Analisar este projeto geral sem Git."; await Until(() => vm.CanSend); vm.SendCommand.Execute(null);
                await Until(() => vm.ActiveCount == 0); Require(worker.Calls == 1 && vm.SelectedConversation!.State == "Concluída", "Projeto sem Git perdeu o chat.");
                await CloseAsync(window);
                var missing = new GitDiagnosticsWindow(new(vm.Project!, new GitRepositoryInspector(searchPath: ""))) { Owner = main }; missing.Show();
                await Until(() => !missing.ViewModel.Busy && missing.ViewModel.Snapshot is not null);
                Require(missing.ViewModel.Snapshot!.State == GitDiagnosticState.GitUnavailable, "Git ausente não foi identificado."); await CloseAsync(missing);
                var lateInspector = new LateInspector(); var lateVm = new GitDiagnosticsViewModel(vm.Project!, lateInspector);
                var late = new GitDiagnosticsWindow(lateVm) { Owner = main }; late.Show(); await lateInspector.Started.Task;
                lateVm.CancelCommand.Execute(null); Require(lateVm.Busy, "Cancelamento liberou a consulta antes do processo parar.");
                lateInspector.Release.SetResult(); await Until(() => !lateVm.Busy);
                Require(lateVm.Snapshot is null && lateVm.Notice.Contains("cancelada") && lateVm.Changes.Count == 0, "Resposta tardia foi publicada após cancelamento.");
                await lateVm.RefreshAsync(); Require(lateVm.Snapshot is null && lateVm.Notice.Contains("Não foi possível"), "Falha virou estado limpo.");
                await lateVm.RefreshAsync(); Require(lateVm.StatusTitle == "Conflitos pendentes" && lateVm.Changes.Single(c => c.Change.IsConflict).IndexText == "Conflito"
                    && lateVm.ChangeSummary.Contains("Preparados: 1 · Na pasta: 0"),
                    "Conflito não foi destacado na interface."); await CloseAsync(late);
                var blocking = new BlockingInspector(); var closing = new GitDiagnosticsWindow(new(vm.Project!, blocking)) { Owner = main }; closing.Show();
                await blocking.Started.Task; closing.Close(); await Until(() => !closing.IsVisible);
                Require(blocking.Cancelled && !closing.ViewModel.RefreshCommand.CanExecute(null), "Fechar não cancelou a consulta pendente.");
                var ownerBlocking = new BlockingInspector(); var owned = new GitDiagnosticsWindow(new(vm.Project!, ownerBlocking)) { Owner = main }; owned.Show();
                await ownerBlocking.Started.Task; main.Close(); await Until(() => !main.IsVisible && !owned.IsVisible);
                Require(ownerBlocking.Cancelled, "Fechar a central deixou uma consulta Git pendente.");
                Require(errors.Errors.Count == 0, "Erros de binding: " + string.Join("\n", errors.Errors));
                Console.WriteLine("PASS: diagnóstico WPF com Git real em pasta de teste, subpasta/raiz, renomeação, mudanças, índice preservado, janela vinculada ao projeto, ausência de Git/repositório, cancelamento/resposta tardia/fechamento e chat sem Git; zero erros de binding. Chat/erros simulados; nenhum modelo chamado.");
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally
            {
                foreach (var window in app.Windows.OfType<GitDiagnosticsWindow>().ToArray()) { await window.ViewModel.StopAsync(); window.Close(); }
                await vm.StopAsync(); if (main.IsVisible) main.Close(); app.Shutdown(exitCode);
            }
        };
        app.Run(main); return exitCode;
    }
    private static async Task GitAsync(string directory, params string[] args)
    {
        var result = await ProcessProbe.RunAsync(new("git.exe", [], "Teste Git"),
            new[] { "-c", "user.name=Sintonia Test", "-c", "user.email=sintonia@example.invalid", "-c", "commit.gpgSign=false",
                "-c", "core.hooksPath=" + Path.Combine(directory, "no-hooks"), "-c", "core.fsmonitor=false", "-c", "core.excludesFile=" }.Concat(args).ToArray(),
            directory, TimeSpan.FromSeconds(10));
        Require(result.ExitCode == 0 && !result.TimedOut && !result.Truncated, "Preparação do Git de teste falhou.");
    }
    private static async Task CloseAsync(GitDiagnosticsWindow window) { window.Close(); await Until(() => !window.IsVisible); }
    private static Button Button(DependencyObject parent, string id)
    {
        if (parent is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            try { return Button(VisualTreeHelper.GetChild(parent, i), id); } catch (KeyNotFoundException) { }
        throw new KeyNotFoundException(id);
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) { if (DateTime.UtcNow >= deadline) throw new TimeoutException("Estado de diagnóstico Git não chegou."); await Task.Delay(25); }
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout(); var surface = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)surface.ActualWidth, (int)surface.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
    private sealed class Worker : IConversationProvider
    {
        public int Calls { get; private set; }
        public ProviderKind Kind => ProviderKind.Codex;
        public Task<ConversationResult> SendAsync(ConversationRequest request, IProgress<ConversationEvent> progress, CancellationToken token)
        {
            Calls++; return Task.FromResult(new ConversationResult("simulated-session", "simulated-model", "SIMULAÇÃO: chat em projeto sem Git.", ConversationOutcome.Completed, []));
        }
    }
    private sealed class LateInspector : IGitRepositoryInspector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;
        public async Task<GitRepositoryDiagnostic> InspectAsync(string directory, CancellationToken token)
        {
            if (++_calls == 2) throw new IOException("SIMULAÇÃO: falha de processo");
            if (_calls == 3) return new(directory, GitDiagnosticState.Available, directory, "test", new('a', 40), false, false, null, null, null,
                [new("conflito.txt", null, 'U', 'U', true, false, "N..."), new("módulo", null, 'M', '.', false, false, "S...")], DateTimeOffset.UtcNow, "SIMULAÇÃO: conflito para verificar apresentação.");
            Started.SetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(5));
            return new(directory, GitDiagnosticState.Available, directory, "test", new('a', 40), false, false, null, null, null, [], DateTimeOffset.UtcNow, "SIMULAÇÃO: resposta tardia.");
        }
    }
    private sealed class BlockingInspector : IGitRepositoryInspector
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<GitRepositoryDiagnostic> InspectAsync(string directory, CancellationToken token)
        {
            Started.SetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, token); } catch (OperationCanceledException) { Cancelled = true; throw; }
            throw new InvalidOperationException();
        }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
