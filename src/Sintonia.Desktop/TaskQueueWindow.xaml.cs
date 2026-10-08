using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class TaskQueueWindow : Window
{
    public TaskQueueViewModel ViewModel => (TaskQueueViewModel)DataContext;
    public TaskQueueWindow(TaskQueueViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        Closing += (_, e) => { if (ViewModel.Busy) e.Cancel = true; };
        Closed += (_, _) => ViewModel.Dispose();
    }
}
