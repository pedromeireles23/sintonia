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
            + "Alterações locais e arquivos ignorados permanecem no original. A preparação não chama modelos. Dependentes aguardam a publicação da combinação validada no projeto.",
            "Preparar worktree", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        viewModel.ConfirmArchive ??= preview => ActionConfirmation.Show(this, "Arquivar worktree integrada",
            $"Entrega: {preview.Publication.Reservation.Delivery.Commit}\nPublicada no projeto: {preview.Publication.Commit}\nBranch preservada: {preview.Publication.Reservation.Delivery.Worktree.Branch}\n\n"
            + $"Pasta atual:\n{preview.Publication.Reservation.Delivery.Worktree.CheckoutDirectory}\n\nArquivo de destino:\n{preview.ArchiveDirectory}\n\n"
            + "Todos os arquivos serão movidos intactos, incluindo configurações, arquivos ignorados e não rastreados. O registro ativo desta worktree será retirado do Git; branch e commits permanecem.\n\n"
            + "O arquivo continua ocupando espaço e não é um checkout Git ativo. Não há restauração ou descarte automático; as pastas de combinação permanecem. Após iniciar a movimentação, o aplicativo aguarda seu resultado mesmo se cancelar ou fechar.", "Arquivar e preservar arquivos");
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
