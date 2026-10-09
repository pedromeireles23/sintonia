using System.Collections.ObjectModel;
using System.Text;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record ValidationHistoryItem(TaskIntegrationValidation Record)
{
    public string Label => $"{Record.StartedAt.ToLocalTime():dd/MM HH:mm:ss} · {TaskValidationViewModel.StateText(Record.State)} · revisão {Record.Configuration.Revision}";
}

public sealed class TaskValidationViewModel : ObservableObject
{
    private readonly TaskIntegrationValidationService _service;
    private readonly TaskIntegrationPreparation _preparation;
    private ValidationHistoryItem? _selected;
    private bool _busy, _stopping;
    private string _notice = "Carregando histórico…";
    private CancellationTokenSource? _stop;
    private Task _operation = Task.CompletedTask;
    public TaskValidationViewModel(WorkspaceProject project, TaskIntegrationPreparation preparation, TaskIntegrationValidationService service)
    {
        Project = project; _preparation = preparation; _service = service;
        ReloadCommand = new(ReloadAsync, ShowError, () => CanChoose);
        ValidateCommand = new(ValidateAsync, ShowError, () => CanChoose && preparation.State == TaskIntegrationPreparationState.Combined);
        CancelCommand = new(() => _stop?.Cancel(), () => Busy && _stop is not null && !_stopping);
    }
    public WorkspaceProject Project { get; }
    public string Context => $"{Project.Name} · {_preparation.Reservation.Delivery.TaskId}\nPasta combinada: {_preparation.CheckoutDirectory}\nÁrvore: {_preparation.Tree}\nDestino: {_preparation.Reservation.Target.Branch} · {_preparation.Reservation.Target.Commit}";
    public ObservableCollection<ValidationHistoryItem> History { get; } = [];
    public ValidationHistoryItem? Selected { get => _selected; set { if (!CanChoose) return; Set(ref _selected, value); Notify(nameof(Details)); } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Refresh(); } }
    public bool CanChoose => !Busy && !_stopping;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public Func<TaskIntegrationValidationPreview, bool>? ConfirmValidation { get; set; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand ValidateCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public string Details
    {
        get
        {
            if (Selected is not { Record: var record }) return "Ainda não há resultado salvo. Confira os critérios antes de iniciar.";
            var text = new StringBuilder($"{StateText(record.State)}\nÁrvore: {record.Preparation.Tree}\nCritérios: revisão {record.Configuration.Revision}\nPasta: {record.Preparation.CheckoutDirectory}\n{record.Error}\n");
            for (var i = 0; i < record.Configuration.Commands.Count; i++)
            {
                var command = record.Configuration.Commands[i];
                text.AppendLine($"\n{i + 1}. {command.Name}\nExecutável: {command.Executable}\nPasta relativa: {command.WorkingDirectory} · Prazo: {command.TimeoutSeconds}s");
                foreach (var argument in command.Arguments) text.AppendLine("Argumento: " + argument);
                var result = record.Results?.SingleOrDefault(r => r.CommandIndex == i);
                if (result is null) { text.AppendLine("Sem resultado salvo para este comando."); continue; }
                text.AppendLine($"{StateText(result.State)} · Código: {result.ExitCode?.ToString() ?? "—"} · {(result.Truncated ? "Saída truncada" : "Saída completa")}");
                text.AppendLine("stdout:\n" + result.StandardOutput); text.AppendLine("stderr:\n" + result.StandardError);
            }
            return text.ToString();
        }
    }
    public Task ReloadAsync() => !CanChoose ? Task.CompletedTask : _operation = ReloadCoreAsync();
    private async Task LoadHistoryAsync()
    {
        var records = (await _service.GetAsync(Project.Id)).Where(v => v.Preparation.Reservation.Id == _preparation.Reservation.Id).ToArray();
        var id = Selected?.Record.Reservation.Id; History.Clear(); foreach (var record in records) History.Add(new(record));
        _selected = History.FirstOrDefault(h => h.Record.Reservation.Id == id) ?? History.LastOrDefault();
        Notify(nameof(Selected)); Notify(nameof(Details));
    }
    private async Task ReloadCoreAsync()
    {
        Busy = true; await Task.Yield();
        try { await LoadHistoryAsync(); Notice = "Histórico da combinação. Aprovação dos testes e integração são etapas separadas."; }
        catch (Exception error) { ShowError(error); }
        finally { Busy = false; }
    }
    public Task ValidateAsync() => !CanChoose || _preparation.State != TaskIntegrationPreparationState.Combined ? Task.CompletedTask : _operation = ValidateCoreAsync();
    private async Task ValidateCoreAsync()
    {
        Busy = true; _stop = new(); Refresh(); await Task.Yield();
        try
        {
            Notice = "Conferindo pasta, árvore, origem, destino e critérios salvos…";
            var preview = await _service.PreviewAsync(Project.Id, _preparation.Reservation.Id, _stop.Token);
            _stop.Token.ThrowIfCancellationRequested(); if (_stopping) return;
            if (ConfirmValidation?.Invoke(preview) != true) { Notice = "Validação não confirmada. Nenhum comando foi iniciado."; return; }
            _stop.Token.ThrowIfCancellationRequested(); Notice = "Executando os critérios na ordem configurada. Aguarde o término ou cancele; o histórico será salvo ao encerrar.";
            var result = await _service.ValidateAsync(Project.Id, preview, _stop.Token);
            Notice = _stop.IsCancellationRequested || _stopping ? "Cancelamento aguardado. Confira o registro persistido."
                : $"Validação encerrada: {StateText(result.State)}. Confira os registros; a entrega ainda precisa de integração.";
        }
        catch (Exception error) { ShowError(error); }
        finally
        {
            _selected = null;
            try { await LoadHistoryAsync(); }
            catch (Exception error) { ShowError(error); }
            _stop.Dispose(); _stop = null; Busy = false;
        }
    }
    public async Task StopAsync() { _stopping = true; _stop?.Cancel(); Refresh(); await _operation; }
    private void ShowError(Exception error) => Notice = error is OperationCanceledException ? "Operação cancelada; confira o histórico e os arquivos preservados." : error.Message;
    private void Refresh() { Notify(nameof(CanChoose)); ReloadCommand?.Refresh(); ValidateCommand?.Refresh(); CancelCommand?.Refresh(); }
    public static string StateText(ValidationState state) => state switch
    { ValidationState.Running => "Executando", ValidationState.Passed => "Passou", ValidationState.Failed => "Falhou", ValidationState.TimedOut => "Prazo excedido", ValidationState.Cancelled => "Cancelada", ValidationState.Interrupted => "Interrompida", _ => "Precisa de atenção" };
}
