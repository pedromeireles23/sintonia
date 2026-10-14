using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class FunctionProfileLibraryViewModel : ObservableObject
{
    private readonly IWorkspaceStore _store;
    private readonly Func<Task> _refreshCentral;
    private readonly Func<WorkspaceFunctionProfile, bool> _confirmDelete;
    private readonly Func<WorkspaceFunctionProfile, Task>? _useChief;
    private WorkspaceFunctionProfile? _selected;
    private string _name = "", _function = "Desenvolvimento", _model = "", _instructions = "", _notice = "Carregando perfis…";
    private ProviderKind _provider;
    private bool _dirty, _busy, _loadingDraft;
    public FunctionProfileLibraryViewModel(IWorkspaceStore store, Func<Task> refreshCentral, Func<WorkspaceFunctionProfile, bool> confirmDelete,
        WorkspaceProject? project = null, Func<WorkspaceFunctionProfile, Task>? useChief = null)
    {
        _store = store; _refreshCentral = refreshCentral; _confirmDelete = confirmDelete;
        Project = project; _useChief = useChief;
        SaveCommand = new(SaveAsync, ShowError, () => CanEdit);
        DeleteCommand = new(DeleteAsync, ShowError, () => CanEdit && _selected is not null && !IsDirty);
        ReloadCommand = new(InitializeAsync, ShowError, () => CanChoose);
        NewCommand = new(() => Load(null), () => CanChoose);
        RevertCommand = new(() => Load(_selected), () => CanEdit && IsDirty);
        UseAsChiefCommand = new(UseAsChiefAsync, ShowError, () => CanUseAsChief);
    }
    public WorkspaceProject? Project { get; }
    public string ScopeTitle => Project is null ? "Biblioteca de modelos" : "Equipe · " + Project.Name;
    public string ScopeDescription => Project is null ? "Modelos para novas equipes de projeto."
        : "Funções, provedores, modelos e instruções exclusivos deste projeto. Escolha um perfil de chefia para o chat principal.";
    public bool CanUseAsChief => CanEdit && !IsDirty && _selected?.FunctionName == PlanProposalFormat.ChiefFunctionName && _useChief is not null;
    public ObservableCollection<WorkspaceFunctionProfile> Profiles { get; } = [];
    public IReadOnlyList<ProviderKind> Providers { get; } = Enum.GetValues<ProviderKind>();
    public IReadOnlyList<string> FunctionNames { get; } = WorkspaceFunctionCatalog.Functions.Select(f => f.Name).ToArray();
    public WorkspaceFunctionProfile? SelectedProfile
    {
        get => _selected;
        set
        {
            if (Busy || value == _selected) return;
            if (IsDirty) { Notice = "Salve ou reverta as alterações antes de abrir outro perfil."; Notify(); return; }
            Load(value);
        }
    }
    public string Name { get => _name; set => Change(ref _name, value); }
    public string FunctionName
    {
        get => _function;
        set
        {
            Change(ref _function, value); Notify(nameof(InstructionLimit)); Notify(nameof(InstructionHint)); Notify(nameof(FunctionSummary));
            // Fill the built-in instructions unless the user already wrote their own.
            if (!_loadingDraft && WorkspaceFunctionCatalog.Find(value) is { } template && WorkspaceFunctionCatalog.IsReplaceable(Instructions))
                Instructions = template.Instructions;
        }
    }
    public string FunctionSummary => WorkspaceFunctionCatalog.Find(FunctionName)?.Summary ?? "Função personalizada: descreva nas instruções o que esperar desta função.";
    public ProviderKind Provider { get => _provider; set => Change(ref _provider, value); }
    public string Model { get => _model; set => Change(ref _model, value); }
    public string Instructions { get => _instructions; set => Change(ref _instructions, value); }
    public int InstructionLimit => FunctionName.Trim() == PlanProposalFormat.ChiefFunctionName ? PlanProposalFormat.ChiefAdditionalInstructionsLimit : 8000;
    public string InstructionHint => $"Até {InstructionLimit} caracteres. A chefia inclui também o formato do plano no pedido.";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public string State => IsDirty ? "Alterações não salvas" : _selected is null ? "Novo perfil" : "Perfil salvo";
    public bool IsDirty { get => _dirty; private set { Set(ref _dirty, value); Refresh(); } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Refresh(); } }
    public bool CanEdit => !Busy;
    public bool CanChoose => !Busy && !IsDirty;
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand DeleteCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public DelegateCommand NewCommand { get; }
    public DelegateCommand RevertCommand { get; }
    public AsyncCommand UseAsChiefCommand { get; }

    public async Task InitializeAsync()
    {
        if (Busy || IsDirty) return;
        Busy = true;
        try
        {
            var id = _selected?.Id; var profiles = await _store.GetFunctionProfilesAsync(Project?.Id);
            Profiles.Clear(); foreach (var profile in profiles) Profiles.Add(profile);
            Load(profiles.FirstOrDefault(p => p.Id == id) ?? profiles.FirstOrDefault());
        }
        finally { Busy = false; }
    }
    private void Load(WorkspaceFunctionProfile? profile)
    {
        _loadingDraft = true;
        try
        {
            _selected = profile; Notify(nameof(SelectedProfile));
            var template = WorkspaceFunctionCatalog.Find("Desenvolvimento")!;
            Name = profile?.Name ?? ""; FunctionName = profile?.FunctionName ?? template.Name;
            Provider = profile?.Provider ?? template.SuggestedProvider; Model = profile?.Model ?? ""; Instructions = profile?.Instructions ?? template.Instructions;
            IsDirty = false;
            Notice = Project is null ? "Modelos da biblioteca local. As equipes existentes conservam suas próprias cópias."
                : "Alterações valem somente para este projeto. Para mudar o chat principal, salve um perfil de chefia e use Usar como chefe do projeto.";
        }
        finally { _loadingDraft = false; Refresh(); }
    }
    public async Task SaveAsync()
    {
        if (Busy) return;
        var draft = WorkspaceFunctionProfiles.Normalize(new(_selected?.Id ?? Guid.NewGuid().ToString(), Name, FunctionName, Provider, Model, Instructions, _selected?.Revision ?? 0, Project?.Id));
        Busy = true;
        try
        {
            var saved = _selected is null ? await _store.CreateFunctionProfileAsync(draft.Name, draft.FunctionName, draft.Provider, draft.Model, draft.Instructions, Project?.Id)
                : await _store.SaveFunctionProfileAsync(draft);
            var previous = _selected; var index = previous is null ? -1 : Profiles.IndexOf(previous);
            if (index >= 0) Profiles[index] = saved; else Profiles.Add(saved);
            Load(saved); Notice = "Perfil salvo. Use Nova com perfil na central para aplicar estes padrões.";
            await _refreshCentral();
        }
        finally { Busy = false; }
    }
    public async Task DeleteAsync()
    {
        if (Busy || IsDirty || _selected is not { } profile || !_confirmDelete(profile)) return;
        Busy = true;
        try
        {
            await _store.DeleteFunctionProfileAsync(profile.Id, profile.Revision, Project?.Id); Profiles.Remove(profile); Load(Profiles.FirstOrDefault());
            Notice = "Perfil excluído. Conversas e tarefas existentes preservam seus parâmetros.";
            await _refreshCentral();
        }
        finally { Busy = false; }
    }
    public async Task UseAsChiefAsync()
    {
        if (!CanUseAsChief || _selected is not { } profile) return;
        Busy = true;
        try { await _useChief!(profile); Notice = "Chefia deste projeto atualizada. O chat principal está pronto na central."; }
        finally { Busy = false; }
    }
    private void Change<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name) && !_loadingDraft) { IsDirty = true; Notice = "Alterações não salvas. Salve o perfil ou reverta antes de trocar."; }
    }
    private void ShowError(Exception exception) => Notice = exception.Message;
    private void Refresh()
    {
        Notify(nameof(CanEdit)); Notify(nameof(CanChoose)); Notify(nameof(State));
        Notify(nameof(CanUseAsChief)); UseAsChiefCommand?.Refresh();
        SaveCommand?.Refresh(); DeleteCommand?.Refresh(); ReloadCommand?.Refresh(); NewCommand?.Refresh(); RevertCommand?.Refresh();
    }
}
