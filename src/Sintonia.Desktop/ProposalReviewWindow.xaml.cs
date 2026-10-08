using System.ComponentModel;
using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class ProposalReviewWindow : Window
{
    public ProposalReviewViewModel ViewModel => (ProposalReviewViewModel)DataContext;
    public ProposalReviewWindow(ProposalReviewViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Closing += ConfirmClose;
    }
    private void ConfirmClose(object? sender, CancelEventArgs e)
    {
        if (ViewModel.Busy) { e.Cancel = true; return; }
        if (ViewModel.IsDirty && MessageBox.Show(this, "Há alterações não salvas neste plano. Descartar e fechar?",
            "Revisão do plano", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
    }
}
