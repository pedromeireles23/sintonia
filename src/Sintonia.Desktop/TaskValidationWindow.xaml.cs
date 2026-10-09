using System.Text;
using System.Windows;
using Sintonia.Core;
using Sintonia.Desktop.ViewModels;

namespace Sintonia.Desktop;

public partial class TaskValidationWindow : Window
{
    private bool _closed, _closing;
    public TaskValidationViewModel ViewModel => (TaskValidationViewModel)DataContext;
    public TaskValidationWindow(TaskValidationViewModel viewModel)
    {
        InitializeComponent(); DataContext = viewModel;
        ViewModel.ConfirmValidation ??= preview => ActionConfirmation.Show(this, "Executar critérios de validação", Describe(preview), "Executar critérios");
        Loaded += async (_, _) => await ViewModel.ReloadAsync();
        Closing += async (_, e) =>
        {
            if (_closed) return;
            e.Cancel = true; if (_closing) return;
            _closing = true; IsEnabled = false; await Task.Yield();
            try { await ViewModel.StopAsync(); } finally { _closed = true; Close(); }
        };
    }
    private static string Describe(TaskIntegrationValidationPreview preview)
    {
        var text = new StringBuilder($"Pasta: {preview.Preparation.CheckoutDirectory}\nÁrvore: {preview.Preparation.Tree}\nRevisão dos critérios: {preview.Configuration.Revision}\n");
        foreach (var command in preview.Configuration.Commands)
        {
            text.AppendLine($"\n{command.Name}\n{command.Executable}\nPasta relativa: {command.WorkingDirectory} · Prazo: {command.TimeoutSeconds}s");
            foreach (var argument in command.Arguments) text.AppendLine("Argumento: " + argument);
        }
        text.AppendLine("\nExecutar os comandos com suas permissões locais e salvar o resultado?"); return text.ToString();
    }
}
