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
        ViewModel.ConfirmPublication ??= validation => ActionConfirmation.Show(this, "Publicar entrega no projeto",
            $"Destino: {validation.Reservation.Target.RepositoryDirectory}\nBranch: {validation.Reservation.Target.Branch}\nCommit atual: {validation.Reservation.Target.Commit}\nEntrega: {validation.Reservation.Delivery.Commit}\nÁrvore validada: {validation.Preparation.Tree}\nCritérios: revisão {validation.Configuration.Revision}\n\nCriar um commit de integração e aplicar essa árvore no destino? As pastas de trabalho e os arquivos ignorados serão preservados. Dependentes serão liberados após a conferência e o registro do resultado.\n\nCancelar impede o início da aplicação. Depois dele, o aplicativo aguarda a operação limitada e salva seu resultado. Não haverá push para remotes.", "Publicar no projeto");
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
