using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class TaskQueueWindow : Window
{
    public TaskQueueViewModel ViewModel => (TaskQueueViewModel)DataContext;
    public TaskQueueWindow(TaskQueueViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        viewModel.ConfirmWorktree ??= worktree => MessageBox.Show(this,
            $"Preparar esta pasta para a tarefa?\n\nPasta: {worktree.WorkingDirectory}\nBranch: {worktree.Branch}\nCommit de base: {worktree.BaseCommit}\n\n"
            + "Alterações locais e arquivos ignorados permanecem no original. A preparação não chama modelos. Aprovar a entrega não integra seus arquivos; dependentes aguardarão integração Git, ainda em desenvolvimento.",
            "Preparar worktree", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        Closing += (_, e) => { if (ViewModel.Busy) e.Cancel = true; };
        Closed += (_, _) => ViewModel.Dispose();
    }
    private void OpenDiffs(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanReviewDiffs || ViewModel.SelectedTask is not { } task) return;
        new TaskDiffWindow(ViewModel.Workspace.CreateTaskDiffReview(ViewModel.Project, task.Record)) { Owner = Owner ?? this }.Show();
    }
}
