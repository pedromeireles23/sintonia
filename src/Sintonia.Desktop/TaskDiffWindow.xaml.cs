using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class TaskDiffWindow : Window
{
    private bool _closed, _closing;
    public TaskDiffViewModel ViewModel => (TaskDiffViewModel)DataContext;
    public TaskDiffWindow(TaskDiffViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        ViewModel.ConfirmDelivery ??= review => MessageBox.Show(this,
            $"Registrar o commit {review.Snapshot.HeadCommit} da tarefa {review.Task.Definition.Title}?\n\nO registro inclui os arquivos versionados de toda a worktree, inclusive fora da subpasta do projeto. Não cria commits nem integra arquivos; dependentes continuam bloqueados.",
            "Registrar commit revisado", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        ViewModel.ConfirmCombination ??= target => MessageBox.Show(this,
            $"Combinar a entrega {ViewModel.Delivery?.Commit} com {target.Commit}?\n\nDestino de referência: {target.RepositoryDirectory}\nBranch: {target.Branch}\n\nSerá criada uma nova worktree separada para conferir o conjunto e seus conflitos. Pastas anteriores serão preservadas. Publicação, testes do projeto e liberação de dependentes continuam pendentes.",
            "Preparar combinação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await ViewModel.RefreshAsync();
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true; if (_closing) return;
            _closing = true; IsEnabled = false; await Task.Yield();
            try { await ViewModel.StopAsync(); }
            finally { _closed = true; Close(); }
        };
    }
    private void OpenValidation(object sender, RoutedEventArgs e)
    {
        try { new TaskValidationWindow(ViewModel.CreateValidationReview()) { Owner = Owner ?? this }.Show(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Validação da combinação", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
