using System.Windows;
using Microsoft.Win32;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class ProjectValidationWindow : Window
{
    private bool _closed, _closing;
    private bool _discardApproved;
    public ProjectValidationViewModel ViewModel => (ProjectValidationViewModel)DataContext;
    public Func<bool>? ConfirmDiscard { get; set; }
    public ProjectValidationWindow(ProjectValidationViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        ConfirmDiscard = () => MessageBox.Show(this, "Descartar as alterações não salvas?", "Critérios de validação", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
        Loaded += async (_, _) => await ViewModel.ReloadAsync();
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true; if (_closing || !ConfirmClose()) return;
            _closing = true; IsEnabled = false; await Task.Yield();
            try { await ViewModel.StopAsync(); } finally { _closed = true; Close(); }
        };
    }
    public bool ConfirmClose()
    {
        if (_discardApproved || !ViewModel.IsDirty) return true;
        return _discardApproved = ConfirmDiscard?.Invoke() == true;
    }
    private void PickExecutable(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanEdit || ViewModel.Selected is not { } draft) return;
        var dialog = new OpenFileDialog { Title = "Executável do critério", Filter = "Executáveis (*.exe)|*.exe", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) draft.Executable = dialog.FileName;
    }
}
