using System.Windows.Threading;
using Sintonia.Core;
using UsageWindow = Sintonia.Core.ProviderUsageWindow;

namespace Sintonia.Desktop.ViewModels;

/// <summary>One plan window: how much remains and when it renews, relative to the current time.</summary>
public sealed class LimitGaugeViewModel(string label) : ObservableObject
{
    private UsageWindow? _window;
    public string Label => label;
    public UsageWindow? Window
    {
        get => _window;
        set
        {
            _window = value;
            foreach (var name in new[] { nameof(Window), nameof(HasData), nameof(RemainingPercent), nameof(IsLow), nameof(RemainingText), nameof(ResetText), nameof(Details) }) Notify(name);
        }
    }
    public bool HasData => Window is not null;
    public int RemainingPercent => Window?.RemainingPercent ?? 0;
    public bool IsLow => Window is { RemainingPercent: < 20 };
    public string RemainingText => Window is null ? "—" : $"{Window.RemainingPercent}%";
    public string ResetText => Window is null ? "sem dados" : Window.ResetsAt is { } reset ? "renova em " + Countdown(reset - DateTimeOffset.Now) : "renovação não informada";
    public string Details => Window is null ? $"{label}: sem dados da conta."
        : $"{label}: {Window.RemainingPercent}% restante ({Window.UsedPercent}% usado)"
          + (Window.ResetsAt is { } reset ? " · renova " + reset.ToLocalTime().ToString("dd/MM HH:mm") : "");
    public void Tick() { Notify(nameof(ResetText)); Notify(nameof(Details)); }
    private static string Countdown(TimeSpan left) => left <= TimeSpan.Zero ? "instantes"
        : left.TotalDays >= 1 ? $"{(int)left.TotalDays}d {left.Hours}h"
        : left.TotalHours >= 1 ? $"{(int)left.TotalHours}h{left.Minutes:00}"
        : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}min";
}

public sealed class ProviderLimitsViewModel(ProviderKind provider) : ObservableObject
{
    private string _status = "Consultando…", _detail = "Consulta de metadados, sem iniciar conversa.";
    private ProviderUsageSnapshot? _snapshot;
    public ProviderKind Provider => provider;
    public LimitGaugeViewModel Session { get; } = new("5 horas");
    public LimitGaugeViewModel Weekly { get; } = new("Semanal");
    public ProviderUsageSnapshot? Snapshot => _snapshot;
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string StatusDetail { get => _detail; private set => Set(ref _detail, value); }
    public bool Blocked => _snapshot?.OrdinaryUsageAllowed == false;
    internal void Begin() { if (_snapshot is null) Status = "Consultando…"; else Status = "Atualizando…"; }
    internal void Apply(ProviderUsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        (Session.Window, Weekly.Window) = snapshot.PlanWindows();
        var time = snapshot.CheckedAt.ToLocalTime().ToString("HH:mm");
        Status = Blocked ? "Uso bloqueado" : snapshot.HasData ? "às " + time : "Indisponível";
        StatusDetail = Blocked ? "O provedor informou que o uso incluído está indisponível agora. Novos envios ao Codex são interrompidos."
            : snapshot.UnavailableReason ?? $"Consultado às {time}. Uso da conta inteira, compartilhado com outros aplicativos.";
        Notify(nameof(Snapshot)); Notify(nameof(Blocked));
    }
    internal void Fail(string reason)
    {
        Status = _snapshot is null ? "Indisponível" : "Falha · dados de " + _snapshot.CheckedAt.ToLocalTime().ToString("HH:mm");
        StatusDetail = reason;
    }
}

/// <summary>Plan limits for every configured provider, refreshed on start, every ten minutes, after each answer and on demand.</summary>
public sealed class SubscriptionLimitsViewModel : ObservableObject
{
    private static readonly TimeSpan AutomaticInterval = TimeSpan.FromMinutes(10), AfterRunDelay = TimeSpan.FromMinutes(1);
    private readonly IReadOnlyDictionary<ProviderKind, IProviderUsageReader> _readers;
    private readonly Func<string> _directory;
    private readonly Dictionary<ProviderKind, Task> _queries = [];
    private readonly Dictionary<ProviderKind, DateTimeOffset> _lastStart = [];
    private readonly HashSet<ProviderKind> _pending = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private DateTimeOffset _lastAutomatic = DateTimeOffset.MinValue;
    private bool _stopping;

    public SubscriptionLimitsViewModel(IEnumerable<IProviderUsageReader> readers, Dispatcher dispatcher, Func<string> directory)
    {
        _readers = readers.GroupBy(r => r.Kind).ToDictionary(g => g.Key, g => g.First());
        _directory = directory;
        Providers = Enum.GetValues<ProviderKind>().Where(_readers.ContainsKey).Select(p => new ProviderLimitsViewModel(p)).ToArray();
        RefreshCommand = new(() => RefreshAsync(), _ => { }, () => !_stopping && Providers.Any(p => !_queries.ContainsKey(p.Provider)));
        _timer = new(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => Tick(), dispatcher) { IsEnabled = false };
    }

    public IReadOnlyList<ProviderLimitsViewModel> Providers { get; }
    public bool HasProviders => Providers.Count > 0;
    public AsyncCommand RefreshCommand { get; }

    public void Start()
    {
        if (_stopping || !HasProviders || _timer.IsEnabled) return;
        _lastAutomatic = DateTimeOffset.UtcNow; _timer.Start(); _ = RefreshAsync();
    }

    public Task RefreshAsync() => Task.WhenAll(Providers.Select(p => RefreshAsync(p.Provider)));

    /// <summary>After an answer the account usage changed. Queries at most once a minute per provider; a closer request waits for the next tick.</summary>
    public void RequestRefresh(ProviderKind provider)
    {
        if (_stopping || !_readers.ContainsKey(provider)) return;
        if (_lastStart.TryGetValue(provider, out var last) && DateTimeOffset.UtcNow - last < AfterRunDelay) { _pending.Add(provider); return; }
        _ = RefreshAsync(provider);
    }

    public Task RefreshAsync(ProviderKind provider)
    {
        if (_stopping || !_readers.ContainsKey(provider)) return Task.CompletedTask;
        if (_queries.TryGetValue(provider, out var running)) return running;
        _pending.Remove(provider);
        var query = QueryAsync(Providers.Single(p => p.Provider == provider));
        if (!query.IsCompleted) { _queries[provider] = query; RefreshCommand.Refresh(); }
        return query;
    }

    private async Task QueryAsync(ProviderLimitsViewModel row)
    {
        _lastStart[row.Provider] = DateTimeOffset.UtcNow;
        row.Begin();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(90));
            var snapshot = await _readers[row.Provider].ReadUsageAsync(_directory(), timeout.Token);
            snapshot.ValidateDefinition();
            if (snapshot.Provider != row.Provider) throw new InvalidOperationException("A consulta retornou outro provedor.");
            if (!_stopping) row.Apply(snapshot);
        }
        catch (OperationCanceledException) when (_stopping) { }
        catch (Exception error)
        {
            if (!_stopping) row.Fail(error is OperationCanceledException ? "A consulta excedeu o prazo. Use Atualizar para tentar de novo."
                : error is ProviderException or ArgumentException ? error.Message
                : "Não foi possível consultar os limites. Confira a instalação, o login e a conexão do provedor.");
        }
        finally
        {
            _queries.Remove(row.Provider);
            RefreshCommand.Refresh();
        }
    }

    private void Tick()
    {
        foreach (var row in Providers) { row.Session.Tick(); row.Weekly.Tick(); }
        if (DateTimeOffset.UtcNow - _lastAutomatic >= AutomaticInterval) { _lastAutomatic = DateTimeOffset.UtcNow; _ = RefreshAsync(); return; }
        foreach (var provider in _pending.ToArray()) RequestRefresh(provider);
    }

    public async Task StopAsync()
    {
        _stopping = true; _timer.Stop(); _lifetime.Cancel(); RefreshCommand.Refresh();
        await Task.WhenAll(_queries.Values.ToArray());
    }
}
