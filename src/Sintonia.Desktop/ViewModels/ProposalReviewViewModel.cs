using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class ProposedTaskViewModel : ObservableObject
{
    private readonly Action _changed;
    private string _title, _function, _model, _instructions, _scope, _dependencies, _criteria;
    private ProviderKind _provider;
    private AccessOption _access;
    public static IReadOnlyList<AccessOption> AccessOptions { get; } = [new("Leitura", ConversationAccess.ReadOnly), new("Alterações", ConversationAccess.WorkspaceWrite)];
    public ProposedTaskViewModel(ProposedTask task, Action changed)
    {
        Id = task.Id; _title = task.Title; _function = task.FunctionName; _provider = task.Provider;
        _model = task.Model ?? ""; _access = AccessOptions.Single(a => a.Value == task.Access);
        _instructions = task.Instructions; _scope = string.Join("\n", task.Scope);
        _dependencies = string.Join(", ", task.Dependencies); _criteria = string.Join("\n", task.AcceptanceCriteria);
        _changed = changed;
    }
    public string Id { get; }
    public string Display => $"{Id} · {Title}";
    public string Title { get => _title; set { Change(ref _title, value); Notify(nameof(Display)); } }
    public string FunctionName { get => _function; set => Change(ref _function, value); }
    public ProviderKind Provider { get => _provider; set => Change(ref _provider, value); }
    public string Model { get => _model; set => Change(ref _model, value); }
    public AccessOption Access { get => _access; set => Change(ref _access, value); }
    public string Instructions { get => _instructions; set => Change(ref _instructions, value); }
    public string Scope { get => _scope; set => Change(ref _scope, value); }
    public string Dependencies { get => _dependencies; set => Change(ref _dependencies, value); }
    public string AcceptanceCriteria { get => _criteria; set => Change(ref _criteria, value); }
    private void Change<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Set(ref field, value, name)) _changed();
    }
    private static string[] Lines(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    public ProposedTask ToDefinition() => new(Id, Title.Trim(), FunctionName.Trim(), Provider,
        string.IsNullOrWhiteSpace(Model) ? null : Model.Trim(), Access.Value, Instructions.Trim(), Lines(Scope),
        Dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Lines(AcceptanceCriteria));
}

public sealed class ProposalReviewViewModel : ObservableObject
{
    private readonly IWorkspaceStore _store;
    private WorkspaceProposal? _selected;
    private ProposedTaskViewModel? _task;
    private string _title = "", _objective = "", _notice = "Carregando propostas…";
    private bool _dirty, _busy;
    public ProposalReviewViewModel(IWorkspaceStore store, WorkspaceProject project)
    {
        _store = store; Project = project;
        SaveDraftCommand = new(() => SaveAsync(ProposalReviewState.Draft), ShowError, () => CanEdit);
        ApproveCommand = new(() => SaveAsync(ProposalReviewState.Approved), ShowError, () => CanEdit);
        ValidateCommand = new(Validate, () => CanEdit);
        RevertCommand = new(() => { if (_selected is { } saved) Load(saved); }, () => CanEdit && IsDirty);
        AddTaskCommand = new(AddTask, () => CanEdit && Tasks.Count < PlanProposalFormat.MaxTasks);
        RemoveTaskCommand = new(RemoveTask, () => CanEdit && SelectedTask is not null && Tasks.Count > 1);
        ReloadCommand = new(InitializeAsync, ShowError, () => !Busy && !IsDirty);
    }
    public WorkspaceProject Project { get; }
    public ObservableCollection<WorkspaceProposal> Proposals { get; } = [];
    public ObservableCollection<ProposedTaskViewModel> Tasks { get; } = [];
    public IReadOnlyList<ProviderKind> Providers { get; } = Enum.GetValues<ProviderKind>();
    public IReadOnlyList<AccessOption> AccessOptions => ProposedTaskViewModel.AccessOptions;
    public WorkspaceProposal? SelectedProposal
    {
        get => _selected;
        set
        {
            if (Busy) return;
            if (value == _selected) return;
            if (IsDirty) { Notice = "Salve o rascunho ou reverta as alterações antes de abrir outro plano."; Notify(); return; }
            _selected = value; Notify();
            if (value is not null) Load(value);
            else { Tasks.Clear(); SelectedTask = null; Title = ""; Objective = ""; IsDirty = false; }
            Refresh();
        }
    }
    public ProposedTaskViewModel? SelectedTask { get => _task; set { Set(ref _task, value); Refresh(); } }
    public string Title { get => _title; set { if (Set(ref _title, value)) MarkChanged(); } }
    public string Objective { get => _objective; set { if (Set(ref _objective, value)) MarkChanged(); } }
    public string State => IsDirty ? "Alterações aguardando revisão" : _selected?.State == ProposalReviewState.Approved ? "Plano aprovado para execução futura" : "Rascunho aguardando revisão";
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool IsDirty { get => _dirty; private set { Set(ref _dirty, value); Refresh(); } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Refresh(); } }
    public bool CanEdit => _selected is not null && !Busy;
    public bool CanChooseProposal => !Busy && !IsDirty;
    public AsyncCommand SaveDraftCommand { get; }
    public AsyncCommand ApproveCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public DelegateCommand ValidateCommand { get; }
    public DelegateCommand RevertCommand { get; }
    public DelegateCommand AddTaskCommand { get; }
    public DelegateCommand RemoveTaskCommand { get; }

    public async Task InitializeAsync()
    {
        if (IsDirty) return;
        Busy = true;
        try
        {
            var proposals = await _store.GetProposalsAsync(Project.Id);
            var selectedId = _selected?.Id;
            Proposals.Clear(); foreach (var proposal in proposals) Proposals.Add(proposal);
            _selected = proposals.FirstOrDefault(p => p.Id == selectedId) ?? proposals.FirstOrDefault();
            Notify(nameof(SelectedProposal));
            if (_selected is not null) Load(_selected);
            else { Tasks.Clear(); SelectedTask = null; Notice = "Ainda não há planos. Escolha a função Chefe do projeto no chat e descreva seu objetivo."; }
        }
        finally { Busy = false; }
    }
    private void Load(WorkspaceProposal proposal)
    {
        _title = proposal.Definition.Title; _objective = proposal.Definition.Objective;
        Notify(nameof(Title)); Notify(nameof(Objective)); Notify(nameof(State));
        Tasks.Clear();
        foreach (var task in proposal.Definition.Tasks) Tasks.Add(new(task, MarkChanged));
        SelectedTask = Tasks.FirstOrDefault(); IsDirty = false;
        Notice = "Revise responsáveis, escopo e critérios. Confirmar registra o plano; as tarefas aguardam a etapa de execução.";
    }
    private PlanProposal Definition() => new(1, Title.Trim(), Objective.Trim(), Tasks.Select(t => t.ToDefinition()).ToArray());
    private void Validate()
    {
        try { PlanProposalFormat.Serialize(Definition()); Notice = "Plano válido: critérios, escopo e dependências conferidos. Os modelos serão verificados ao executar."; }
        catch (PlanValidationException exception) { ShowError(exception); }
    }
    public async Task SaveAsync(ProposalReviewState state)
    {
        if (!CanEdit || _selected is null) return;
        var proposal = _selected with { Definition = Definition(), State = state };
        PlanProposalFormat.Serialize(proposal.Definition);
        Busy = true;
        try
        {
            var saved = await _store.SaveProposalAsync(proposal);
            var index = Proposals.IndexOf(_selected);
            if (index >= 0) Proposals[index] = saved;
            _selected = saved; Notify(nameof(SelectedProposal));
            Load(saved);
            Notice = state == ProposalReviewState.Approved ? "Plano confirmado e salvo. As tarefas aguardam a etapa de execução." : "Rascunho salvo. Revise e confirme quando estiver pronto.";
        }
        finally { Busy = false; }
    }
    private void MarkChanged()
    {
        IsDirty = true; Notice = "Alterações não salvas. Valide e salve o rascunho ou confirme o plano.";
    }
    private void AddTask()
    {
        var number = 1;
        while (Tasks.Any(t => t.Id == "task-" + number)) number++;
        var task = new ProposedTaskViewModel(new("task-" + number, "Nova tarefa", "Desenvolvimento", ProviderKind.Codex,
            null, ConversationAccess.ReadOnly, "", [], [], []), MarkChanged);
        Tasks.Add(task); SelectedTask = task; MarkChanged();
    }
    private void RemoveTask()
    {
        if (SelectedTask is not { } task) return;
        Tasks.Remove(task); SelectedTask = Tasks.FirstOrDefault(); MarkChanged();
    }
    private void ShowError(Exception exception) => Notice = exception.Message;
    private void Refresh()
    {
        Notify(nameof(CanEdit)); Notify(nameof(State));
        Notify(nameof(CanChooseProposal));
        SaveDraftCommand?.Refresh(); ApproveCommand?.Refresh(); ReloadCommand?.Refresh(); ValidateCommand?.Refresh();
        RevertCommand?.Refresh(); AddTaskCommand?.Refresh(); RemoveTaskCommand?.Refresh();
    }
}
