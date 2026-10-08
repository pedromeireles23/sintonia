using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sintonia.Core;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Desktop;

public partial class WorkspaceWindow : Window
{
    private bool _closed;
    private bool _closing;
    public WorkspaceViewModel ViewModel => (WorkspaceViewModel)DataContext;
    public WorkspaceWindow(WorkspaceViewModel? viewModel = null)
    {
        InitializeComponent();
        var store = new SqliteWorkspaceStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "workspace.db"));
        DataContext = viewModel ?? new WorkspaceViewModel(store,
            new WorkspaceChatService(store, [new CodexConversationProvider(), new ClaudeConversationProvider()]), Dispatcher,
            PickDirectory, InspectAsync);
        Loaded += async (_, _) => await ViewModel.InitializeAsync();
        Closing += CloseAsync;
    }
    private string? PickDirectory()
    {
        var dialog = new OpenFolderDialog { Title = "Escolha a pasta do projeto", Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }
    private static async Task<ProviderCapabilities> InspectAsync(ProviderKind provider, string directory, CancellationToken token)
    {
        if (provider == ProviderKind.Codex) return await new CodexConversationProvider().InspectAsync(directory, token);
        await new ClaudeConversationProvider().CheckSubscriptionAsync(directory, token);
        return new(provider, [new("opus", "Opus (alias do CLI)", false), new("sonnet", "Sonnet (alias do CLI)", false), new("fable", "Fable (alias do CLI)", false)], [],
            ["Aliases anunciados pelo Claude instalado. Você também pode informar um identificador de modelo; o acesso é conferido ao enviar."]);
    }
    private void OpenDemo(object sender, RoutedEventArgs e) => new MainWindow().Show();
    private async void CloseAsync(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        IsEnabled = false;
        await Task.Yield();
        try { await ViewModel.StopAsync(); }
        finally { _closed = true; Close(); }
    }
}
