using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Sintonia.Core;
using Sintonia.Desktop.ViewModels;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Providers;
using Sintonia.Infrastructure.Git;
using Sintonia.Infrastructure.Validation;

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
        var worktrees = new GitTaskWorktreeManager();
        var deliveries = new TaskDeliveryService(store, new GitTaskDeliveryInspector(worktrees));
        DataContext = viewModel ?? new WorkspaceViewModel(store,
            new WorkspaceChatService(store, [new CodexConversationProvider(), new ClaudeConversationProvider()], worktrees), Dispatcher,
            PickDirectory, InspectAsync, new TaskWorktreeService(store, worktrees), new TaskDiffService(store, new GitTaskDiffReader(worktrees)),
            deliveries, new TaskIntegrationPreparationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskIntegrationPreparer(worktrees)),
            new TaskIntegrationValidationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskIntegrationValidationInspector(worktrees), new ValidationCommandRunner()),
            new TaskPublicationService(store, deliveries, new RepositoryIntegrationLock(), new GitTaskPublisher(worktrees)),
            new TaskWorktreeCleanupService(store, new RepositoryIntegrationLock(), worktrees));
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
    private void OpenGitDiagnostics(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Project is { } project)
            new GitDiagnosticsWindow(new(project, new GitRepositoryInspector())) { Owner = this }.Show();
    }
    private async void OpenProposals(object sender, RoutedEventArgs e)
    {
        try { new ProposalReviewWindow(await ViewModel.LoadProposalReviewAsync()) { Owner = this }.Show(); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Revisão do plano", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void OpenTasks(object sender, RoutedEventArgs e)
    {
        try { new TaskQueueWindow(await ViewModel.LoadTaskQueueAsync()) { Owner = this }.Show(); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Fila de tarefas", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void OpenFunctionProfiles(object sender, RoutedEventArgs e)
    {
        try
        {
            var library = await ViewModel.LoadFunctionProfileLibraryAsync(profile => MessageBox.Show(this,
                $"Excluir o perfil {profile.Name}? Conversas e tarefas existentes preservam seus parâmetros.", "Perfis de função",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes);
            new FunctionProfileLibraryWindow(library) { Owner = this }.Show();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, "Perfis de função", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    private async void CloseAsync(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        foreach (var editor in OwnedWindows.OfType<ProjectValidationWindow>())
            if (!editor.ConfirmClose()) return;
        _closing = true;
        IsEnabled = false;
        await Task.Yield();
        try
        {
            await Task.WhenAll(OwnedWindows.OfType<GitDiagnosticsWindow>().Select(window => window.ViewModel.StopAsync())
                .Concat(OwnedWindows.OfType<TaskDiffWindow>().Select(window => window.ViewModel.StopAsync()))
                .Concat(OwnedWindows.OfType<TaskValidationWindow>().Select(window => window.ViewModel.StopAsync()))
                .Concat(OwnedWindows.OfType<ProjectValidationWindow>().Select(window => window.ViewModel.StopAsync()))
                .Concat(OwnedWindows.OfType<TaskQueueWindow>().Select(window => window.ViewModel.StopAsync())).Append(ViewModel.StopAsync()));
        }
        finally { _closed = true; Close(); }
    }
    private void OpenValidationConfiguration(object sender, RoutedEventArgs e)
    {
        try { new ProjectValidationWindow(ViewModel.CreateProjectValidation()) { Owner = this }.Show(); }
        catch (Exception error) { MessageBox.Show(this, error.Message, "Critérios de validação", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
