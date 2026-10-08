using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class FunctionProfileLibraryWindow : Window
{
    public FunctionProfileLibraryViewModel ViewModel => (FunctionProfileLibraryViewModel)DataContext;
    public FunctionProfileLibraryWindow(FunctionProfileLibraryViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Closing += (_, e) =>
        {
            if (ViewModel.Busy) { e.Cancel = true; return; }
            if (ViewModel.IsDirty && MessageBox.Show(this, "Há alterações não salvas no perfil. Descartar e fechar?", "Perfis de função",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
        };
    }
}
