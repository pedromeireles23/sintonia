using System.Windows;

namespace Sintonia.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is null)
        {
            MainWindow = new MainWindow();
            MainWindow.Show();
        }
    }
}
