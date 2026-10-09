using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class TaskQueueWindow : Window
{
    private bool _closing, _closeReady;
    public TaskQueueViewModel ViewModel => (TaskQueueViewModel)DataContext;
    public TaskQueueWindow(TaskQueueViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        ViewModel.ConfirmRevision ??= details => ActionConfirmation.Show(this, "Atualizar plano encaminhado", details, "Aplicar proposta revisada");
        viewModel.ConfirmWorktree ??= worktree => MessageBox.Show(this,
            $"Preparar esta pasta para a tarefa?\n\nPasta: {worktree.WorkingDirectory}\nBranch: {worktree.Branch}\nCommit de base: {worktree.BaseCommit}\n\n"
            + "Alterações locais e arquivos ignorados permanecem no original. A preparação não chama modelos. Aprovar a entrega não integra seus arquivos; dependentes aguardarão integração Git, ainda em desenvolvimento.",
            "Preparar worktree", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        Closing += async (_, e) =>
        {
            if (_closeReady || !ViewModel.Busy) return;
            e.Cancel = true;
            if (_closing) return;
            _closing = true;
            try { await ViewModel.StopAsync(); }
            finally { _closeReady = true; Close(); }
        };
        Closed += (_, _) => ViewModel.Dispose();
    }
    private void OpenDiffs(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanReviewDiffs || ViewModel.SelectedTask is not { } task) return;
        new TaskDiffWindow(ViewModel.Workspace.CreateTaskDiffReview(ViewModel.Project, task.Record)) { Owner = Owner ?? this }.Show();
    }
}
