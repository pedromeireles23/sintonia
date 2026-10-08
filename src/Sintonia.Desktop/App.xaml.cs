using System.Windows;

namespace Sintonia.Desktop;

public partial class App : Application
{
    private Mutex? _workspaceMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (MainWindow is null)
        {
            _workspaceMutex = new Mutex(true, "Local\\Sintonia-Workspace-" + Environment.UserName, out var firstInstance);
            if (!firstInstance)
            {
                MessageBox.Show("O Sintonia já está aberto nesta sessão do Windows.", "Sintonia");
                Shutdown();
                return;
            }
            MainWindow = new WorkspaceWindow();
            MainWindow.Show();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _workspaceMutex?.Dispose();
        base.OnExit(e);
    }
}
