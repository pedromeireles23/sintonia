using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sintonia.Core;
using Sintonia.Desktop;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure;

namespace Sintonia.Desktop.SmokeTests;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.FirstOrDefault() == "--workspace") return WorkspaceSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/workspace-smoke");
        if (args.FirstOrDefault() == "--proposals") return ProposalSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/proposal-smoke");
        if (args.FirstOrDefault() == "--queue") return TaskQueueSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/queue-smoke");
        if (args.FirstOrDefault() == "--profiles") return FunctionProfileSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/profile-smoke");
        if (args.FirstOrDefault() == "--git") return GitDiagnosticsSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/git-smoke");
        if (args.FirstOrDefault() == "--worktrees") return TaskWorktreeSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/worktree-smoke");
        if (args.FirstOrDefault() == "--diffs") return TaskDiffSmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/diff-smoke");
        if (args.FirstOrDefault() == "--deliveries") return TaskDeliverySmoke.Run(args.Skip(1).FirstOrDefault() ?? "artifacts/delivery-smoke");
        if (args.FirstOrDefault() == "--workspace-real") return WorkspaceRealSmoke.Run();
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-smoke");
        Directory.CreateDirectory(output);
        var errors = new BindingListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var window = new MainWindow();
        ((MainViewModel)window.DataContext).Dispose();
        var vm = new MainViewModel(window.Dispatcher, DemoScenario.Create(TimeSpan.FromMilliseconds(40)));
        window.DataContext = vm;
        var exitCode = 1;
        window.Loaded += async (_, _) =>
        {
            try
            {
                Require(new System.Windows.Interop.WindowInteropHelper(window).Handle != IntPtr.Zero, "Janela nativa não foi criada.");
                Capture(window, Path.Combine(output, "01-initial.png"));
                var dispatch = FindButton(window, "DispatchQueue");
                Require(dispatch.IsEnabled, "Botão de distribuição desabilitado.");
                dispatch.Command.Execute(null);
                await Until(() => vm.RunningCount == 2);
                Capture(window, Path.Combine(output, "02-running.png"));
                await Until(() => vm.ReviewCount == 2 && vm.RunningCount == 0 && vm.DispatchCommand.CanExecute(null));
                Require(vm.Tasks.Skip(2).All(t => t.State == WorkTaskState.Queued), "Dependência liberada antes da aprovação.");
                Capture(window, Path.Combine(output, "03-review.png"));

                foreach (var task in vm.Tasks)
                {
                    vm.SelectedTask = task;
                    await Until(() => task.CanApprove && vm.ApproveCommand.CanExecute(null));
                    var approve = FindButton(window, "ApproveDelivery");
                    Require(approve.IsEnabled, "Aprovação não vinculada à tarefa selecionada.");
                    approve.Command.Execute(null);
                    await Until(() => task.State == WorkTaskState.Completed && vm.ApproveCommand.CanExecute(null) == (vm.SelectedTask?.CanApprove == true));
                }
                await Until(() => vm.RunningCount == 0);
                Require(vm.ApprovedCount == 5 && vm.Sessions.Count == 5, "Fluxo completo inconsistente.");
                Capture(window, Path.Combine(output, "04-completed.png"));
                window.Width = window.MinWidth;
                window.Height = window.MinHeight;
                await Task.Delay(50);
                Capture(window, Path.Combine(output, "05-minimum-size.png"));
                Require(errors.Messages.Count == 0, $"Erros de binding: {string.Join("\n", errors.Messages)}");
                Console.WriteLine("PASS: janela WPF nativa, bindings, 2 execuções simuladas, revisão, 5 aprovações e sessões. Capturas: " + output);
                exitCode = 0;
            }
            catch (Exception exception) { Console.Error.WriteLine(exception); }
            finally { window.Close(); app.Shutdown(); }
        };
        app.Run(window);
        return exitCode;
    }

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("A interface não atingiu o estado esperado.");
            await Task.Delay(10);
        }
        await Task.Delay(20); // Allow bindings and command notifications to reach the visual tree.
    }

    private static Button FindButton(DependencyObject root, string id)
    {
        if (root is Button button && AutomationProperties.GetAutomationId(button) == id) return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            try { return FindButton(VisualTreeHelper.GetChild(root, index), id); }
            catch (KeyNotFoundException) { }
        }
        throw new KeyNotFoundException(id);
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class BindingListener : TraceListener
    {
        public List<string> Messages { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}
