using System.Windows;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(Dispatcher);
        Closed += (_, _) => (DataContext as IDisposable)?.Dispose();
    }
}
