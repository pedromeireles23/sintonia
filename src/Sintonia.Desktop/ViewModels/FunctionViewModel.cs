using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class FunctionViewModel(FunctionProfile profile, Action<string, ProviderKind> assign) : ObservableObject
{
    private ProviderKind _provider = profile.Provider;
    private bool _canAssign = true;
    public string Id => profile.Id;
    public string Name => profile.Name;
    public string Instructions => profile.Instructions;
    public ProviderKind[] Providers { get; } = Enum.GetValues<ProviderKind>();
    public bool CanAssign { get => _canAssign; private set => Set(ref _canAssign, value); }
    public ProviderKind Provider
    {
        get => _provider;
        set { if (_provider == value) return; assign(Id, value); }
    }

    public void Update(FunctionProfile function, bool active)
    {
        _provider = function.Provider;
        Notify(nameof(Provider));
        CanAssign = !active;
    }
}
