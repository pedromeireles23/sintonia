using System.Collections.ObjectModel;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record ProviderUsageWindowRow(string Name, Sintonia.Core.ProviderUsageWindow? Window)
{
    public bool HasData => Window is not null;
    public int UsedPercent => Window?.UsedPercent ?? 0;
    public string Percentages => Window is null ? "Percentuais indisponíveis" : $"{Window.UsedPercent}% usado · {Window.RemainingPercent}% restante";
    public string Duration => Window?.DurationMinutes is not { } minutes ? "Duração não informada"
        : minutes % 1440 == 0 ? DescribeDuration(minutes / 1440, "dia", "dias")
        : minutes % 60 == 0 ? DescribeDuration(minutes / 60, "hora", "horas") : DescribeDuration(minutes, "minuto", "minutos");
    public string Reset => Window?.ResetsAt is not { } reset ? "Renovação não informada"
        : "Renovação prevista: " + reset.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz");
    private static string DescribeDuration(long count, string singular, string plural) => $"Janela de {count} {(count == 1 ? singular : plural)}";
}

public sealed record ProviderUsageBucketRow(ProviderUsageBucket Bucket)
{
    public string Name => string.IsNullOrWhiteSpace(Bucket.Name) ? Bucket.Id : Bucket.Name;
    public IReadOnlyList<ProviderUsageWindowRow> Windows { get; } = [new("Janela principal", Bucket.Primary), new("Janela secundária", Bucket.Secondary)];
}

public sealed class ProviderUsageViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<ProviderKind, IProviderUsageReader> _readers;
    private CancellationTokenSource? _refresh;
    private Task _operation = Task.CompletedTask;
    private ProviderUsageSnapshot? _snapshot;
    private ProviderKind _provider;
    private bool _busy, _stopping;
    private string _notice = "Atualize para consultar os dados disponíveis na conta do provedor.";
    public ProviderUsageViewModel(WorkspaceProject project, ProviderKind provider, IEnumerable<IProviderUsageReader> readers)
    {
        Project = project; _provider = provider; _readers = readers.ToDictionary(reader => reader.Kind);
        RefreshCommand = new(RefreshAsync, _ => Notice = "Não foi possível concluir a consulta.", () => CanConfigure);
        CancelCommand = new(() => _refresh?.Cancel(), () => Busy && !_stopping);
    }
    public WorkspaceProject Project { get; }
    public IReadOnlyList<ProviderKind> Providers { get; } = [ProviderKind.Codex, ProviderKind.Claude];
    public ObservableCollection<ProviderUsageBucketRow> Buckets { get; } = [];
    public ProviderKind Provider
    {
        get => _provider;
        set
        {
            if (!CanConfigure || !Providers.Contains(value) || !Set(ref _provider, value)) return;
            ClearSnapshot(); Notice = "Atualize para consultar os dados desta conta."; RefreshProperties();
        }
    }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); RefreshProperties(); } }
    public bool CanConfigure => !Busy && !_stopping;
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public ProviderUsageSnapshot? Snapshot => _snapshot;
    public string StatusTitle => Busy ? "Consultando uso…" : Snapshot is null ? "Sem consulta atual" : Snapshot.HasData ? "Dados da conta consultados" : "Quota indisponível";
    public string CheckedAt => Snapshot is null ? "Ainda não consultado" : "Consultado em " + Snapshot.CheckedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss zzz");
    public string Allowance => Snapshot?.OrdinaryUsageAllowed switch
    {
        true => "Uso incluído permitido pelo provedor no momento da consulta.",
        false => "Uso incluído indisponível. O Sintonia interrompe novos envios ao Codex quando o provedor confirma esse estado.",
        _ => "Disponibilidade de uso incluído não informada. O provedor aplica seus limites ao enviar."
    };
    public string WindowNotice => Snapshot is null ? "Atualize para obter uma consulta completa."
        : Buckets.Count == 0 ? "Nenhuma janela de consumo foi informada." : "Percentuais e renovação fornecidos pelo provedor; campos ausentes ficam indisponíveis.";
    public AsyncCommand RefreshCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public Task RefreshAsync()
    {
        if (!CanConfigure) return Task.CompletedTask;
        _refresh = new(); ClearSnapshot(); Busy = true;
        Notice = "Consultando a conta, sem iniciar conversa ou chamar modelos.";
        return _operation = RefreshCoreAsync(Provider, _refresh);
    }
    private async Task RefreshCoreAsync(ProviderKind provider, CancellationTokenSource stop)
    {
        await Task.Yield();
        try
        {
            var result = await _readers[provider].ReadUsageAsync(Project.Directory, stop.Token);
            if (_stopping || stop.IsCancellationRequested) { Notice = "Consulta cancelada. Atualize para obter dados atuais."; return; }
            result.ValidateDefinition();
            if (result.Provider != provider) throw new InvalidOperationException();
            _snapshot = result;
            foreach (var bucket in result.Buckets) Buckets.Add(new(bucket));
            Notice = result.UnavailableReason ?? "Consulta concluída. O uso é compartilhado por esta conta em outros projetos e aplicativos.";
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { Notice = "Consulta cancelada. Atualize para obter dados atuais."; }
        catch (Exception)
        { Notice = "Não foi possível consultar o uso. Confira a instalação, login e conexão do provedor e tente atualizar."; }
        finally { _refresh = null; stop.Dispose(); Busy = false; }
    }
    public async Task StopAsync()
    { _stopping = true; _refresh?.Cancel(); RefreshProperties(); await _operation; }
    private void ClearSnapshot() { _snapshot = null; Buckets.Clear(); }
    private void RefreshProperties()
    {
        foreach (var property in new[] { nameof(Snapshot), nameof(CanConfigure), nameof(StatusTitle), nameof(CheckedAt), nameof(Allowance), nameof(WindowNotice) }) Notify(property);
        RefreshCommand.Refresh(); CancelCommand.Refresh();
    }
}
