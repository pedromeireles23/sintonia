using System.Collections.ObjectModel;
using System.Windows.Threading;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record FunctionOption(string Name, string Instructions);
public sealed record AccessOption(string Name, ConversationAccess Value);
/// <summary>Key is the text the editable picker shows and the identifier sent to the provider.</summary>
public sealed record ModelOption(string Key, string Name, string Detail, string Description = "")
{
    public bool HasDescription => Description.Length > 0;
}
public sealed record ReasoningOption(string? Id, string Name, string Description = "")
{
    public bool HasDescription => Description.Length > 0;
}

public sealed class ChatMessageViewModel(string author, string text, string state = "", bool showUsage = false) : ObservableObject
{
    private string _text = text;
    private string _state = state;
    private RunTokenUsage? _tokenUsage;
    public string Author => author;
    public string Text { get => _text; set => Set(ref _text, value); }
    public string State { get => _state; set => Set(ref _state, value); }
    public bool ShowUsage => showUsage;
    public RunTokenUsage? TokenUsage { get => _tokenUsage; set { Set(ref _tokenUsage, value); Notify(nameof(TokenUsageText)); } }
    public string TokenUsageText => TokenUsage?.Describe() ?? "Consumo de tokens indisponível nesta execução.";
}

public sealed class ConversationViewModel(WorkspaceConversation record) : ObservableObject
{
    private WorkspaceConversation _record = record;
    private string _state = "Pronta";
    private bool _running;
    public WorkspaceConversation Record { get => _record; set { _record = value; Notify(); Notify(nameof(Title)); Notify(nameof(Description)); } }
    public string Title => Record.Title;
    public string Description => $"{Record.Provider} · {ModelSummary} · {Record.FunctionName}" + (Record.IsTask ? " · Tarefa da fila" : "");
    private string ModelSummary => (Record.Model ?? Record.SelectedModel ?? "padrão da instalação")
        + ((Record.EffectiveReasoningEffort ?? Record.ReasoningEffort) is { } effort ? " · " + effort : "");
    public string State { get => _state; set => Set(ref _state, value); }
    public bool Running { get => _running; set => Set(ref _running, value); }
    public bool Loaded { get; set; }
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = [];
    public ObservableCollection<string> Events { get; } = [];
}

public sealed class WorkspaceViewModel : ObservableObject
{
    private const string DefaultModel = "Padrão da instalação";
    private readonly IWorkspaceStore _store;
    private readonly WorkspaceChatService _chat;
    private readonly TaskWorktreeService? _worktrees;
    private readonly TaskDiffService? _diffs;
    private readonly TaskDeliveryService? _deliveries;
    private readonly TaskIntegrationPreparationService? _preparations;
    private readonly TaskIntegrationValidationService? _validations;
    private readonly TaskPublicationService? _publications;
    private readonly TaskWorktreeCleanupService? _cleanups;
    private readonly HashSet<Task> _gitJobs = [];
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _pickDirectory;
    private readonly Func<ProviderKind, string, CancellationToken, Task<ProviderCapabilities>> _inspect;
    private readonly Dictionary<string, ConversationViewModel> _sessions = [];
    private readonly Dictionary<string, (CancellationTokenSource Stop, Task Task, WorkspaceExecutionSlot Slot)> _jobs = [];
    private readonly Dictionary<string, ProjectExecutionSettings> _executionSettings = [];
    private int _sessionLimit = ProjectExecutionSettings.Minimum;
    private int _taskAttemptLimit = 3;
    private string _executionTimeout = "300";
    private bool _savingSessionLimit;
    private Task _settingsSave = Task.CompletedTask;
    private Task _tokenRefresh = Task.CompletedTask;
    private readonly Dictionary<string, ProjectTokenBudget> _tokenBudgets = [];
    private string _tokenLimit = "";
    private string _tokenReservation = "1000";
    private readonly SemaphoreSlim _permissionGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool>? _permissionAnswer;
    private ConversationPermission? _permission;
    private ConversationQuestionsViewModel? _questions;
    private WorkspaceProject? _project;
    private ConversationViewModel? _selected;
    private ProviderKind _provider;
    private string _model = DefaultModel;
    private string _prompt = "";
    private string _instructions = "";
    private FunctionOption _function;
    private AccessOption _access;
    private WorkspaceFunctionProfile? _profile;
    private long _profileRevision;
    private string _notice = "Adicione uma pasta de projeto para começar.";
    private bool _ready;
    private bool _stopping;
    private bool _loadingProject;
    private long _projectRevision;
    private ReasoningOption _reasoning = new(null, DefaultModel);
    // Metadata-only catalogs, cached per provider and folder because Codex can read project configuration.
    private readonly Dictionary<(ProviderKind Provider, string Directory), ProviderCapabilities> _catalogs = [];
    private readonly Dictionary<(ProviderKind Provider, string Directory), string> _catalogErrors = [];
    private readonly Dictionary<(ProviderKind Provider, string Directory), Task> _catalogQueries = [];

    public WorkspaceViewModel(IWorkspaceStore store, WorkspaceChatService chat, Dispatcher dispatcher,
        Func<string?> pickDirectory, Func<ProviderKind, string, CancellationToken, Task<ProviderCapabilities>> inspect,
        TaskWorktreeService? worktrees = null, TaskDiffService? diffs = null, TaskDeliveryService? deliveries = null,
        TaskIntegrationPreparationService? preparations = null, TaskIntegrationValidationService? validations = null, TaskPublicationService? publications = null,
        TaskWorktreeCleanupService? cleanups = null)
    {
        _store = store; _chat = chat; _dispatcher = dispatcher; _pickDirectory = pickDirectory; _inspect = inspect;
        _worktrees = worktrees;
        _diffs = diffs;
        _deliveries = deliveries;
        _preparations = preparations;
        _validations = validations;
        _publications = publications;
        _cleanups = cleanups;
        _function = Functions[0]; _access = AccessOptions[0];
        AddProjectCommand = new(AddPickedProjectAsync, ShowError, () => Ready && !_stopping);
        RefreshModelsCommand = new(RefreshModelsAsync, ShowError, () => Ready && !_stopping && !_loadingProject
            && CatalogKey is { } key && !_catalogQueries.ContainsKey(key));
        NewConversationCommand = new(NewConversation, () => Ready && Project is not null && !_stopping);
        SendCommand = new(() => StartSend(), () => CanSend);
        CancelCommand = new(() => { if (SelectedConversation is { } current && _jobs.TryGetValue(current.Record.Id, out var job)) job.Stop.Cancel(); }, () => SelectedConversation?.Running == true);
        AllowPermissionCommand = new(() => _permissionAnswer?.TrySetResult(true), () => Permission is not null);
        DenyPermissionCommand = new(() => _permissionAnswer?.TrySetResult(false), () => Permission is not null);
        ApplyFunctionProfileCommand = new(ApplyFunctionProfileAsync, ShowError, () => CanApplyFunctionProfile);
        RefreshFunctionProfilesCommand = new(RefreshFunctionProfilesAsync, ShowError, () => CanManageFunctionProfiles);
        SaveSessionLimitCommand = new(SaveSessionLimitAsync, ShowError, () => CanEditSessionLimit && LimitsChanged);
        RefreshTokenBudgetCommand = new(RefreshTokenBudgetAsync, ShowError, () => Ready && Project is not null && !_loadingProject && !_stopping);
    }

    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];
    public ObservableCollection<ModelOption> Models { get; } = [new(DefaultModel, DefaultModel, "Segue a configuração do provedor.")];
    public ObservableCollection<ReasoningOption> ReasoningOptions { get; } = [new(null, DefaultModel)];
    public ObservableCollection<WorkspaceFunctionProfile> FunctionProfiles { get; } = [];
    public WorkspaceFunctionProfile? SelectedFunctionProfile { get => _profile; set { Set(ref _profile, value); RefreshCommands(); } }
    public IReadOnlyList<ProviderKind> Providers { get; } = Enum.GetValues<ProviderKind>();
    public ObservableCollection<FunctionOption> Functions { get; } =
    [
        new("Conversa", ""),
        new("Chefe do projeto", "Atue como chefe deste projeto. Delimite tarefas pequenas com critérios verificáveis, dependências e indicação de Codex ou Claude. Proponha um plano para revisão; não execute nem delegue automaticamente e não aprove entregas pelo usuário."),
        new("Desenvolvimento", "Implemente a tarefa com mudanças pequenas. Examine as instruções do projeto, preserve alterações existentes e valide o resultado. Não use outros agentes sem pedido explícito."),
        new("Revisão", "Revise o escopo solicitado, explique problemas concretos e critérios de validação. Não altere arquivos durante a revisão."),
        new("Personalizada", "")
    ];
    public IReadOnlyList<AccessOption> AccessOptions { get; } = [new("Conversa e leitura", ConversationAccess.ReadOnly), new("Permitir alterações no projeto", ConversationAccess.WorkspaceWrite)];
    public WorkspaceProject? Project
    {
        get => _project;
        set
        {
            if (!Set(ref _project, value)) return;
            Prompt = "";
            Notify(nameof(ProjectName)); Notify(nameof(ProjectPath)); RefreshCommands();
            _ = LoadProjectAsync(value);
        }
    }
    public string ProjectName => Project?.Name ?? "Sua central de projetos";
    public string ProjectPath => Project?.Directory ?? "Codex e Claude, com conversas e histórico em um só lugar.";
    public ConversationViewModel? SelectedConversation
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!Set(ref _selected, value)) return;
            if (previous is not null) previous.PropertyChanged -= SelectedConversationChanged;
            if (value is not null) value.PropertyChanged += SelectedConversationChanged;
            Prompt = "";
            if (value is not null)
            {
                // The picker shows the user's choice; the confirmed version stays in Record.Model.
                _provider = value.Record.Provider; _model = SavedModelChoice(value.Record) ?? DefaultModel;
                _instructions = value.Record.Instructions;
                _function = FindFunction(value.Record.FunctionName);
                _access = AccessOptions.Single(a => a.Value == value.Record.Access);
                Notify(nameof(Provider)); Notify(nameof(Model)); Notify(nameof(Instructions)); Notify(nameof(Function)); Notify(nameof(Access));
                ResetModels(value.Record.ReasoningEffort);
                _ = LoadConversationAsync(value);
            }
            NotifyModelInfo();
            Notify(nameof(Messages)); Notify(nameof(Events)); Notify(nameof(ConversationTitle)); Notify(nameof(CanConfigure)); Notify(nameof(CanEditFunction));
            if (value is not null) Notice = value.Record.IsTask ? "Conversa de tarefa. Inicie novas tentativas e revise a entrega pela Fila de tarefas." : $"{value.State} · {value.Record.Provider}. Continue pelo campo de mensagem ou abra outra conversa.";
            RefreshCommands();
        }
    }
    public ObservableCollection<ChatMessageViewModel>? Messages => SelectedConversation?.Messages;
    public ObservableCollection<string>? Events => SelectedConversation?.Events;
    public string ConversationTitle => SelectedConversation?.Title ?? "Nova conversa";
    public ProviderKind Provider
    {
        get => _provider;
        set
        {
            if (!Set(ref _provider, value)) return;
            Model = DefaultModel;
            ResetModels(null);
        }
    }
    public string Model
    {
        get => _model;
        set { if (Set(ref _model, value)) { ResetReasoningOptions(_reasoning.Id); NotifyModelInfo(); } }
    }
    public ReasoningOption Reasoning
    {
        get => _reasoning;
        // A list refresh briefly pushes null from the picker; the rebuilt list restores the choice.
        set { if (value is not null && Set(ref _reasoning, value)) NotifyModelInfo(); }
    }
    public bool CanChooseModel => CanEditFunction;
    public bool CanChooseReasoning => CanChooseModel && ReasoningOptions.Count > 1;
    public string ReasoningHint => ReasoningOptions.Count > 1 ? "Níveis anunciados pelo catálogo da instalação para este modelo. O provedor confere o suporte antes do turno."
        : ReasoningModel() is not null ? "Este modelo não anuncia níveis de reasoning no catálogo da instalação."
        : "O catálogo ainda não informou níveis para este modelo. Use Atualizar para consultar a instalação.";
    private (ProviderKind Provider, string Directory)? CatalogKey => Project is { } project ? (Provider, project.Directory) : null;
    private ProviderCapabilities? Catalog => CatalogKey is { } key && _catalogs.TryGetValue(key, out var catalog) ? catalog : null;
    private string? ExplicitModel => string.IsNullOrWhiteSpace(Model) || Model == DefaultModel ? null : Model.Trim();
    private static string? SavedModelChoice(WorkspaceConversation record) =>
        record.UsesInstallationDefault == true ? null : record.SelectedModel ?? record.Model;

    public string InstallationModelText
    {
        get
        {
            if (CatalogKey is not { } key) return "Selecione um projeto";
            if (_catalogQueries.ContainsKey(key)) return "Consultando a instalação…";
            if (_catalogErrors.ContainsKey(key)) return "Consulta indisponível";
            if (Catalog is not { } catalog) return "Ainda não consultado";
            var id = InstallationDefaultModelId(catalog);
            var entry = catalog.Models.FirstOrDefault(m => m.Id == id);
            return id is null ? "Não informado pela instalação" : entry is null || entry.Name == id ? id : $"{entry.Name} · {id}";
        }
    }
    public string InstallationModelDetail
    {
        get
        {
            if (CatalogKey is not { } key) return "";
            if (_catalogQueries.ContainsKey(key)) return "Lendo catálogo e configuração. Nenhuma mensagem é enviada ao modelo.";
            if (_catalogErrors.TryGetValue(key, out var error)) return error;
            if (Catalog is not { } catalog) return "Use Atualizar para consultar o catálogo.";
            var entry = catalog.Models.FirstOrDefault(m => m.Id == InstallationDefaultModelId(catalog));
            var effort = catalog.Defaults?.ReasoningEffort ?? entry?.DefaultReasoningEffort;
            var source = catalog.Defaults is not { } defaults ? "padrão do catálogo; configuração não informada"
                : defaults.ModelSource == defaults.ReasoningSource ? defaults.ModelSource
                : $"modelo: {defaults.ModelSource}; reasoning: {defaults.ReasoningSource}";
            // Claude aliases resolve per CLI version. Older catalogs name it in the first description segment ("Opus 5 · ...");
            // newer ones put it in the display name, which the text above already shows.
            var version = Provider == ProviderKind.Claude && entry?.Description is { } description && description.Contains(" · ", StringComparison.Ordinal)
                ? description.Split(" · ")[0] + " · " : "";
            return $"{version}Reasoning {(effort is null ? "padrão do CLI" : ModelSettings.EffortLabel(effort))} · {source}";
        }
    }
    public string ConversationModelText => SelectedConversation?.Record is not { } record ? "Nova conversa"
        : record.Model ?? "Sessão ainda não confirmada";
    public string ConversationModelDetail
    {
        get
        {
            if (SelectedConversation is not { } session) return "A versão e o reasoning efetivos aparecem quando o provedor conectar a sessão.";
            var record = session.Record;
            var reasoning = record.Model is null ? session.Running ? "Aguardando o provedor confirmar a sessão." : "Confirmação no próximo envio."
                : record.EffectiveReasoningEffort is { } confirmed ? $"Reasoning {ModelSettings.EffortLabel(confirmed)}, confirmado na última sessão."
                : record.ReasoningEffort is { } requested ? $"Reasoning {ModelSettings.EffortLabel(requested)} solicitado; o CLI não confirma o nível."
                : Catalog?.Defaults?.ReasoningEffort is { } configured ? $"Reasoning padrão da instalação ({ModelSettings.EffortLabel(configured)}); o {record.Provider} não confirmou o nível."
                : $"Reasoning padrão; o {record.Provider} não informou o nível.";
            if (record.IsTask) return reasoning + " Tarefa da fila: definido pelo plano aprovado.";
            if (session.Running) return reasoning + " Executando com esta configuração.";
            var changed = ExplicitModel != SavedModelChoice(record) || Reasoning.Id != record.ReasoningEffort;
            return changed ? reasoning + " Nova escolha vale no próximo envio." : reasoning;
        }
    }
    public string SelectedModelDescription => Models.FirstOrDefault(m => m.Key == Model) is { } option
        ? option.Description.Length > 0 ? $"{option.Name} — {option.Description}" : option.Detail
        : "Identificador informado manualmente. O provedor confere o acesso ao enviar.";
    public string Prompt { get => _prompt; set { if (Set(ref _prompt, value)) RefreshCommands(); } }
    public string Instructions { get => _instructions; set => Set(ref _instructions, value); }
    public FunctionOption Function { get => _function; set { if (Set(ref _function, value)) { Instructions = value.Instructions; if (IsChief) Access = AccessOptions[0]; Notify(nameof(CanEditAccess)); } } }
    public bool IsChief => Function.Name == PlanProposalFormat.ChiefFunctionName;
    public bool CanEditAccess => CanEditFunction && !IsChief;
    public AccessOption Access { get => _access; set { Set(ref _access, IsChief ? AccessOptions[0] : value); RefreshCommands(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool Ready { get => _ready; private set { Set(ref _ready, value); RefreshCommands(); } }
    public int ActiveCount => _jobs.Count;
    public IReadOnlyList<int> SessionLimits { get; } = [3, 4, 5, 6, 7];
    public int SessionLimit { get => _sessionLimit; set { Set(ref _sessionLimit, value); RefreshCommands(); } }
    public int SavedSessionLimit => Project is { } project ? LimitFor(project.Id) : ProjectExecutionSettings.Minimum;
    public IReadOnlyList<int> AttemptLimits { get; } = [1, 2, 3];
    public int TaskAttemptLimit { get => _taskAttemptLimit; set { Set(ref _taskAttemptLimit, value); RefreshCommands(); } }
    public string ExecutionTimeout { get => _executionTimeout; set { Set(ref _executionTimeout, value); RefreshCommands(); } }
    public string TokenLimit { get => _tokenLimit; set { Set(ref _tokenLimit, value); RefreshCommands(); } }
    public string TokenReservation { get => _tokenReservation; set { Set(ref _tokenReservation, value); RefreshCommands(); } }
    public string TokenBudgetDescription => Project is { } project && _tokenBudgets.TryGetValue(project.Id, out var budget)
        ? budget.Describe() : "Consumo ainda não consultado. O banco confere o limite antes de cada início.";
    private bool HasTokenCapacity(string projectId) => !_tokenBudgets.TryGetValue(projectId, out var budget) || budget.CanAdmit(out _);
    public int AttemptLimitFor(string projectId) => _executionSettings.TryGetValue(projectId, out var settings) ? settings.MaxAttempts : 3;
    private bool LimitsChanged => Project is { } project && _executionSettings.TryGetValue(project.Id, out var settings)
        && (settings.MaxConcurrentSessions != SessionLimit || settings.MaxAttempts != TaskAttemptLimit || ExecutionTimeout != settings.MaxExecutionSeconds.ToString()
            || TokenLimit != (settings.MaxReportedTokens?.ToString() ?? "") || TokenReservation != settings.TokenReservation.ToString());
    public string SessionUsage => Project is { } project
        ? SessionUsageFor(project.Id)
        : $"{ActiveCount}/7 na central";
    public string SessionUsageFor(string projectId) =>
        $"{_jobs.Values.Count(j => j.Slot.ProjectId == projectId)}/{LimitFor(projectId)} no projeto · {ActiveCount}/7 na central";
    public bool CanEditSessionLimit => Ready && Project is not null && !_loadingProject && !_savingSessionLimit && !_stopping;
    private int LimitFor(string projectId) => _executionSettings.TryGetValue(projectId, out var settings)
        ? settings.MaxConcurrentSessions : ProjectExecutionSettings.Minimum;
    public bool CanConfigure => SelectedConversation is null && !_stopping && !_loadingProject;
    public bool CanReviewProposals => Ready && Project is not null && !_stopping;
    public bool CanManageFunctionProfiles => Ready && !_stopping;
    public bool CanApplyFunctionProfile => CanManageFunctionProfiles && Project is not null && !_loadingProject && SelectedFunctionProfile is not null;
    public bool CanEditFunction => SelectedConversation?.Running != true && SelectedConversation?.Record.IsTask != true && !_stopping && !_loadingProject;
    public bool CanSend => Ready && Project is not null && !_stopping && !_loadingProject && SelectedConversation?.Running != true
        && HasTokenCapacity(Project.Id)
        && SelectedConversation?.Record.IsTask != true
        && (SelectedConversation is null || SelectedConversation.Loaded)
        && WorkspaceExecutionPolicy.CanAdmit(WorkspaceExecutionSlot.Create(Project,
            new(SelectedConversation?.Record.Id ?? "new", Project.Id, "", Provider, Model, null, Function.Name, Instructions, Access.Value)),
            _jobs.Values.Select(j => j.Slot).ToArray(), LimitFor(Project.Id), out _)
        && !string.IsNullOrWhiteSpace(Prompt);
    public ConversationPermission? Permission { get => _permission; private set { Set(ref _permission, value); Notify(nameof(HasPermission)); RefreshCommands(); } }
    public bool HasPermission => Permission is not null;
    public ConversationQuestionsViewModel? Questions { get => _questions; private set { Set(ref _questions, value); Notify(nameof(HasQuestions)); } }
    public bool HasQuestions => Questions is not null;
    public AsyncCommand AddProjectCommand { get; }
    public AsyncCommand RefreshModelsCommand { get; }
    public DelegateCommand NewConversationCommand { get; }
    public DelegateCommand SendCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand AllowPermissionCommand { get; }
    public DelegateCommand DenyPermissionCommand { get; }
    public AsyncCommand ApplyFunctionProfileCommand { get; }
    public AsyncCommand RefreshFunctionProfilesCommand { get; }
    public AsyncCommand SaveSessionLimitCommand { get; }
    public AsyncCommand RefreshTokenBudgetCommand { get; }

    public async Task InitializeAsync()
    {
        try
        {
            await _store.InitializeAsync();
            await _store.RecoverInterruptedRunsAsync();
            await RefreshFunctionProfilesAsync();
            foreach (var project in await _store.GetProjectsAsync()) Projects.Add(project);
            Ready = true;
            Project = Projects.FirstOrDefault();
            if (Project is not null) Notice = "Escolha uma conversa ou inicie outra. O envio usa a assinatura conectada ao CLI.";
        }
        catch (Exception exception) { ShowError(exception); }
    }

    public async Task AddProjectAsync(string directory)
    {
        var project = await _store.AddProjectAsync(directory);
        if (Projects.All(p => p.Id != project.Id)) Projects.Add(project);
        Project = Projects.Single(p => p.Id == project.Id);
        Notice = "Projeto adicionado. Escolha a IA, o modelo e envie seu primeiro pedido.";
    }
    private async Task AddPickedProjectAsync() { if (_pickDirectory() is { } directory) await AddProjectAsync(directory); }

    private async Task LoadProjectAsync(WorkspaceProject? project)
    {
        var revision = ++_projectRevision;
        _loadingProject = true;
        SelectedConversation = null;
        Conversations.Clear();
        if (project is null) { _loadingProject = false; RefreshCommands(); return; }
        try
        {
            var settings = await _store.GetProjectExecutionSettingsAsync(project.Id);
            if (revision != _projectRevision || _stopping) return;
            _executionSettings[project.Id] = settings; SessionLimit = settings.MaxConcurrentSessions; TaskAttemptLimit = settings.MaxAttempts; ExecutionTimeout = settings.MaxExecutionSeconds.ToString();
            TokenLimit = settings.MaxReportedTokens?.ToString() ?? ""; TokenReservation = settings.TokenReservation.ToString();
            await QueueTokenBudgetRefreshAsync(project);
            if (revision != _projectRevision || _stopping) return;
            var records = await _store.GetConversationsAsync(project.Id);
            if (revision != _projectRevision || _stopping) return;
            foreach (var record in records)
            {
                if (!_sessions.TryGetValue(record.Id, out var session)) _sessions[record.Id] = session = new(record);
                Conversations.Add(session);
                if (!session.Running)
                {
                    var runs = await _store.GetRunsAsync(record.Id);
                    if (revision != _projectRevision || _stopping) return;
                    session.State = StateText(runs.LastOrDefault()?.State);
                }
            }
            SelectedConversation ??= Conversations.FirstOrDefault();
            ResetModels(_reasoning.Id);
        }
        catch (Exception exception) { ShowError(exception); }
        finally { if (revision == _projectRevision) { _loadingProject = false; RefreshCommands(); } }
    }

    public Task SaveSessionLimitAsync() => _settingsSave = SaveSessionLimitCoreAsync();
    private async Task SaveSessionLimitCoreAsync()
    {
        if (!CanEditSessionLimit || Project is not { } project) return;
        if (!int.TryParse(ExecutionTimeout, out var seconds)) throw new ArgumentException("Use um prazo inteiro entre 1 e 300 segundos.");
        long? tokenLimit = null;
        if (!string.IsNullOrWhiteSpace(TokenLimit))
        {
            if (!long.TryParse(TokenLimit, out var parsedLimit)) throw new ArgumentException("Use um limite de tokens inteiro ou deixe vazio para desativar.");
            tokenLimit = parsedLimit;
        }
        if (!long.TryParse(TokenReservation, out var reservation)) throw new ArgumentException("Use uma reserva de tokens inteira por início.");
        var settings = _executionSettings[project.Id] with { MaxConcurrentSessions = SessionLimit, MaxAttempts = TaskAttemptLimit, MaxExecutionSeconds = seconds,
            MaxReportedTokens = tokenLimit, TokenReservation = reservation };
        settings.Validate();
        _savingSessionLimit = true; RefreshCommands();
        try
        {
            var saved = await _store.SaveProjectExecutionSettingsAsync(settings);
            _executionSettings[project.Id] = saved;
            if (Project?.Id == project.Id && !_stopping)
            {
                SessionLimit = saved.MaxConcurrentSessions;
                TaskAttemptLimit = saved.MaxAttempts; ExecutionTimeout = saved.MaxExecutionSeconds.ToString();
                TokenLimit = saved.MaxReportedTokens?.ToString() ?? ""; TokenReservation = saved.TokenReservation.ToString();
                Notice = $"Limites salvos: {saved.MaxConcurrentSessions} sessões, {saved.MaxAttempts} tentativas por tarefa e {saved.MaxExecutionSeconds}s por execução. Sessões já ativas conservam seus parâmetros.";
            }
            await QueueTokenBudgetRefreshAsync(project);
        }
        catch (InvalidOperationException exception)
        {
            var current = await _store.GetProjectExecutionSettingsAsync(project.Id);
            _executionSettings[project.Id] = current;
            await QueueTokenBudgetRefreshAsync(project);
            throw new InvalidOperationException($"Não foi possível aplicar o limite. Em vigor: {current.MaxConcurrentSessions}. Confira a escolha antes de aplicar novamente. " + exception.Message, exception);
        }
        finally { _savingSessionLimit = false; RefreshCommands(); }
    }

    public async Task RefreshExecutionSettingsAsync(WorkspaceProject project)
    {
        _executionSettings[project.Id] = await _store.GetProjectExecutionSettingsAsync(project.Id);
        await QueueTokenBudgetRefreshAsync(project);
        RefreshCommands();
    }

    public Task RefreshTokenBudgetAsync() => Project is { } project ? QueueTokenBudgetRefreshAsync(project) : Task.CompletedTask;
    private Task QueueTokenBudgetRefreshAsync(WorkspaceProject project) =>
        _tokenRefresh = RefreshTokenBudgetCoreAsync(_tokenRefresh, project);
    private async Task RefreshTokenBudgetCoreAsync(Task previous, WorkspaceProject project)
    {
        await previous;
        if (_stopping) return;
        try
        {
            var budget = await _store.GetProjectTokenBudgetAsync(project.Id);
            if (!_stopping) _tokenBudgets[project.Id] = budget;
        }
        catch (Exception exception)
        {
            _tokenBudgets.Remove(project.Id);
            if (!_stopping && Project?.Id == project.Id) ShowError(exception);
        }
        RefreshCommands();
    }

    private async Task LoadConversationAsync(ConversationViewModel session)
    {
        if (session.Loaded) return;
        try
        {
            var runs = await _store.GetRunsAsync(session.Record.Id);
            var events = await _store.GetEventsAsync(session.Record.Id);
            if (session.Loaded) return;
            foreach (var run in runs)
            {
                session.Messages.Add(new("Você", run.Prompt));
                session.Messages.Add(new(session.Record.Provider.ToString(), run.Response ?? "", StateText(run.State), showUsage: true) { TokenUsage = run.TokenUsage });
                if (run.Error is not null) session.Messages.Add(new("Sintonia", run.Error));
            }
            foreach (var ev in events.TakeLast(200)) session.Events.Add(ev.Text);
            session.Loaded = true;
            RefreshCommands();
        }
        catch (Exception exception) { ShowError(exception); }
    }

    private void NewConversation()
    {
        SelectedConversation = null; Prompt = ""; Model = DefaultModel; ResetModels(null);
        Notice = "Escolha a IA, o modelo e o reasoning. Uma sessão nova será criada ao enviar.";
    }
    private void ResetModels(string? reasoning)
    {
        // Clearing an editable picker can push empty text back; keep the user's choice across the rebuild.
        var model = _model;
        var catalog = Catalog;
        Models.Clear();
        Models.Add(new(DefaultModel, DefaultModel, DefaultModelDetail(catalog)));
        IEnumerable<ProviderModel> entries = catalog?.Models ?? (Provider == ProviderKind.Claude
            ? [new("opus", "Claude Opus · alias", false), new("sonnet", "Claude Sonnet · alias", false), new("fable", "Claude Fable · alias", false)] : []);
        var installationDefault = catalog is null ? null : InstallationDefaultModelId(catalog);
        foreach (var entry in entries)
        {
            var efforts = entry.ReasoningEfforts is { Count: > 0 } list ? " · reasoning " + string.Join(", ", list.Select(e => e.Id)) : "";
            var marker = entry.Id == installationDefault ? " · padrão da instalação" : entry.IsDefault ? " · padrão do catálogo" : "";
            Models.Add(new(entry.Id, entry.Name, entry.Id + marker + efforts, entry.Description));
        }
        if (!string.IsNullOrWhiteSpace(model) && Models.All(m => m.Key != model))
            Models.Add(new(model, model, "Identificador informado; o provedor confere o acesso ao enviar."));
        if (_model != model) { _model = model; Notify(nameof(Model)); }
        ResetReasoningOptions(reasoning);
        NotifyModelInfo();
        EnsureCatalog();
    }
    private void ResetReasoningOptions(string? keep)
    {
        var model = ReasoningModel();
        ReasoningOptions.Clear();
        var installation = Catalog?.Defaults?.ReasoningEffort ?? model?.DefaultReasoningEffort;
        ReasoningOptions.Add(new(null, installation is null ? DefaultModel : $"Padrão · {installation}",
            "Não envia um nível; o provedor usa a configuração da instalação."));
        foreach (var effort in model?.ReasoningEfforts ?? []) ReasoningOptions.Add(new(effort.Id, ModelSettings.EffortLabel(effort.Id), effort.Description));
        // Without catalog data, keep a saved choice visible instead of silently dropping it.
        if (keep is not null && model is null && ReasoningOptions.All(o => o.Id != keep))
            ReasoningOptions.Add(new(keep, ModelSettings.EffortLabel(keep), "Escolha salva; o provedor confere o suporte antes do turno."));
        _reasoning = ReasoningOptions.FirstOrDefault(o => o.Id == keep) ?? ReasoningOptions[0];
        Notify(nameof(Reasoning)); Notify(nameof(CanChooseReasoning)); Notify(nameof(ReasoningHint));
    }
    private ProviderModel? ReasoningModel()
    {
        if (Catalog is not { } catalog) return null;
        if (ExplicitModel is { } id) return catalog.Models.FirstOrDefault(m => m.Id == id);
        return catalog.Models.FirstOrDefault(m => m.Id == InstallationDefaultModelId(catalog));
    }
    private static string? InstallationDefaultModelId(ProviderCapabilities catalog) =>
        catalog.Defaults?.Model ?? catalog.Models.FirstOrDefault(m => m.IsDefault)?.Id;
    private string DefaultModelDetail(ProviderCapabilities? catalog)
    {
        if (catalog is null) return "Segue a configuração do provedor.";
        var id = InstallationDefaultModelId(catalog);
        var effort = catalog.Defaults?.ReasoningEffort;
        var tool = Provider == ProviderKind.Codex ? "do Codex" : "do Claude Code";
        return id is null ? $"Segue a configuração {tool}." : $"Usa {id}{(effort is null ? "" : " · " + effort)} da configuração {tool}.";
    }
    private void SelectedConversationChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        NotifyModelInfo(); Notify(nameof(CanChooseModel)); Notify(nameof(CanChooseReasoning));
    }
    private void NotifyModelInfo()
    {
        Notify(nameof(InstallationModelText)); Notify(nameof(InstallationModelDetail));
        Notify(nameof(ConversationModelText)); Notify(nameof(ConversationModelDetail)); Notify(nameof(SelectedModelDescription));
    }
    private void EnsureCatalog()
    {
        if (CatalogKey is { } key && !_stopping && !_catalogs.ContainsKey(key) && !_catalogErrors.ContainsKey(key)) _ = QueryCatalogAsync(key);
    }
    private Task QueryCatalogAsync((ProviderKind Provider, string Directory) key)
    {
        if (_catalogQueries.TryGetValue(key, out var running)) return running;
        var query = QueryCatalogCoreAsync(key);
        if (!query.IsCompleted) { _catalogQueries[key] = query; NotifyModelInfo(); RefreshModelsCommand.Refresh(); }
        return query;
    }
    private async Task QueryCatalogCoreAsync((ProviderKind Provider, string Directory) key)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            _catalogs[key] = await _inspect(key.Provider, key.Directory, timeout.Token);
            _catalogErrors.Remove(key);
        }
        catch (Exception exception)
        {
            _catalogErrors[key] = exception is OperationCanceledException ? "A consulta foi cancelada ou excedeu 60 segundos. Use Atualizar para tentar novamente." : exception.Message;
        }
        finally
        {
            _catalogQueries.Remove(key);
            if (!_stopping && CatalogKey == key) ResetModels(_reasoning.Id);
            RefreshModelsCommand.Refresh();
        }
    }
    private FunctionOption FindFunction(string name)
    {
        var function = Functions.FirstOrDefault(f => f.Name == name);
        if (function is not null) return function;
        function = new(name, ""); Functions.Add(function); return function;
    }
    public async Task RefreshFunctionProfilesAsync()
    {
        var revision = ++_profileRevision; var profiles = await _store.GetFunctionProfilesAsync();
        if (revision != _profileRevision || _stopping) return;
        var id = SelectedFunctionProfile?.Id;
        FunctionProfiles.Clear(); foreach (var profile in profiles) FunctionProfiles.Add(profile);
        SelectedFunctionProfile = profiles.FirstOrDefault(p => p.Id == id) ?? profiles.FirstOrDefault();
    }
    public async Task ApplyFunctionProfileAsync()
    {
        if (!CanApplyFunctionProfile || SelectedFunctionProfile is not { } selected) return;
        var projectId = Project!.Id;
        var current = (await _store.GetFunctionProfilesAsync()).SingleOrDefault(p => p.Id == selected.Id);
        if (_stopping || Project?.Id != projectId || SelectedFunctionProfile?.Id != selected.Id) return;
        if (current is null || current.Revision != selected.Revision)
        {
            await RefreshFunctionProfilesAsync(); Notice = "O perfil foi alterado ou excluído. Confira a lista atualizada antes de aplicar."; return;
        }
        var prompt = Prompt; NewConversation(); Provider = current.Provider; Model = current.Model ?? DefaultModel;
        Function = FindFunction(current.FunctionName); Instructions = current.Instructions; Access = AccessOptions[0];
        Prompt = prompt; ResetModels(null);
        Notice = $"Perfil {current.Name} aplicado à nova conversa em leitura. Confira o modelo e as instruções antes de enviar.";
    }
    public async Task<FunctionProfileLibraryViewModel> LoadFunctionProfileLibraryAsync(Func<WorkspaceFunctionProfile, bool> confirmDelete)
    {
        var library = new FunctionProfileLibraryViewModel(_store, RefreshFunctionProfilesAsync, confirmDelete);
        await library.InitializeAsync(); return library;
    }
    private async Task RefreshModelsAsync()
    {
        if (CatalogKey is not { } key) return;
        // Keep the previous catalog usable until the new answer arrives.
        _catalogErrors.Remove(key);
        await QueryCatalogAsync(key);
        if (CatalogKey != key || _stopping) return;
        Notice = _catalogErrors.TryGetValue(key, out var error) ? error
            : $"Catálogo {key.Provider} atualizado: {_catalogs[key].Models.Count} modelos. Nenhuma mensagem foi enviada ao modelo; o acesso da assinatura é conferido ao enviar.";
    }

    private void StartSend()
    {
        if (!CanSend) return;
        var prompt = Prompt; Prompt = "";
        var project = Project!;
        var session = SelectedConversation;
        var model = ExplicitModel;
        var reasoning = Reasoning.Id;
        if (session is null)
        {
            var title = prompt.Split('\n')[0].Trim();
            if (title.Length > 52) title = title[..52] + "…";
            session = new(new(Guid.NewGuid().ToString(), project.Id, title, Provider, model,
                null, Function.Name, Instructions, Access.Value, ReasoningEffort: reasoning, UsesInstallationDefault: model is null, SelectedModel: model)) { Loaded = true };
            _sessions.Add(session.Record.Id, session); Conversations.Insert(0, session); SelectedConversation = session;
        }
        else session.Record = session.Record with { FunctionName = Function.Name, Instructions = Instructions, Access = Access.Value,
            SelectedModel = model, UsesInstallationDefault = model is null, ReasoningEffort = reasoning };
        session.Running = true; session.State = "Executando";
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Reserve the view immediately so rapid clicks cannot launch the same conversation twice.
        var slot = WorkspaceExecutionSlot.Create(project, session.Record);
        _jobs.Add(session.Record.Id, (stop, Task.CompletedTask, slot));
        var task = SendCoreAsync(project, session, prompt, stop);
        _jobs[session.Record.Id] = (stop, task, slot);
        RefreshCommands();
    }

    private async Task SendCoreAsync(WorkspaceProject project, ConversationViewModel session, string prompt, CancellationTokenSource stop, string? taskId = null)
    {
        await Task.Yield();
        var isChief = taskId is null && session.Record.FunctionName == PlanProposalFormat.ChiefFunctionName;
        var answer = new ChatMessageViewModel(session.Record.Provider.ToString(), "", "Executando", showUsage: true);
        var finished = false;
        session.Messages.Add(new("Você", prompt)); session.Messages.Add(answer);
        var progress = new Progress<ConversationEvent>(ev =>
        {
            if (ev.Kind == ConversationEventKind.TokenUsage && !finished) answer.TokenUsage = ev.TokenUsage;
            if (ev.Kind == ConversationEventKind.Session)
            {
                session.Record = session.Record with { NativeSessionId = ev.NativeSessionId, Model = ev.Model, EffectiveReasoningEffort = ev.ReasoningEffort };
                if (!finished && !_stopping) _ = QueueTokenBudgetRefreshAsync(project);
            }
            if (ev.Kind == ConversationEventKind.TextDelta && answer.Text.Length < 256_000) answer.Text += ev.Text;
            else if (ev.Kind != ConversationEventKind.TextDelta)
            {
                session.Events.Add(ev.Text);
                while (session.Events.Count > 200) session.Events.RemoveAt(0);
            }
        });
        try
        {
            await _store.SaveConversationAsync(session.Record);
            Task<ConversationQuestionResponse?> AskQuestions(ConversationQuestionRequest questions, CancellationToken token) =>
                AskQuestionsAsync(questions, session.Title, token);
            var run = taskId is null ? await _chat.SendAsync(project, session.Record, prompt, progress, stop.Token, AskPermissionAsync, AskQuestions)
                : await _chat.SendTaskAsync(project.Id, taskId, progress, stop.Token, AskPermissionAsync, AskQuestions);
            finished = true; answer.TokenUsage = run.TokenUsage;
            answer.Text = run.Response ?? ""; answer.State = session.State = StateText(run.State);
            if (run.Error is not null) session.Messages.Add(new("Sintonia", run.Error));
            var saved = (await _store.GetConversationsAsync(project.Id)).Single(c => c.Id == session.Record.Id);
            session.Record = saved;
            Notice = run.State == ChatRunState.Completed ? "Resposta salva. Você pode continuar esta conversa ou abrir outra." : run.Error ?? StateText(run.State);
            if (isChief && run.State == ChatRunState.Completed)
            {
                try
                {
                    await _store.CreateProposalAsync(project.Id, run.Id);
                    Notice = "Proposta salva para revisão. Abra Revisar planos para editar as tarefas e confirmar.";
                }
                catch (Exception exception)
                {
                    session.Events.Add("Proposta não importada: " + exception.Message);
                    Notice = "Resposta salva, mas a proposta não pôde ser importada. " + exception.Message;
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            answer.State = session.State = "Cancelada";
            Notice = "Envio cancelado antes da execução. Nenhuma resposta do provedor foi registrada.";
        }
        catch (Exception exception)
        {
            answer.State = session.State = "Falha";
            session.Messages.Add(new("Sintonia", exception.Message)); ShowError(exception);
        }
        finally
        {
            await QueueTokenBudgetRefreshAsync(project);
            session.Running = false; _jobs.Remove(session.Record.Id); stop.Dispose(); RefreshCommands();
        }
    }

    public async Task<ProposalReviewViewModel> LoadProposalReviewAsync()
    {
        var project = Project ?? throw new InvalidOperationException("Selecione um projeto para revisar seus planos.");
        // Recover the gap between a saved response and proposal import after an application interruption.
        foreach (var conversation in await _store.GetConversationsAsync(project.Id))
            foreach (var run in await _store.GetRunsAsync(conversation.Id))
                if (run.State == ChatRunState.Completed && PlanProposalFormat.TryParseResponse(run.Response, out _, out _))
                    await _store.CreateProposalAsync(project.Id, run.Id);
        var review = new ProposalReviewViewModel(_store, project);
        await review.InitializeAsync();
        return review;
    }

    public async Task<TaskQueueViewModel> LoadTaskQueueAsync()
    {
        var queue = new TaskQueueViewModel(_store, this, Project ?? throw new InvalidOperationException("Selecione um projeto."));
        await queue.InitializeAsync(); return queue;
    }

    public async Task<ConversationViewModel> GetTaskConversationAsync(WorkspaceProject project, WorkspaceTask task)
    {
        var record = (await _store.GetConversationsAsync(project.Id)).Single(c => c.Id == task.ConversationId && c.IsTask);
        if (!_sessions.TryGetValue(record.Id, out var session)) _sessions[record.Id] = session = new(record);
        if (!session.Running) session.Record = record;
        if (Project?.Id == project.Id && Conversations.All(c => c.Record.Id != record.Id)) Conversations.Insert(0, session);
        await LoadConversationAsync(session); return session;
    }

    public async Task StartTaskAsync(WorkspaceProject project, WorkspaceTask task, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping) throw new InvalidOperationException("O aplicativo está encerrando.");
        var session = await GetTaskConversationAsync(project, task);
        cancellationToken.ThrowIfCancellationRequested();
        if (_stopping || _jobs.ContainsKey(session.Record.Id)) throw new InvalidOperationException("A conversa já está executando ou o aplicativo está encerrando.");
        var slot = WorkspaceExecutionSlot.Create(project, session.Record, task.Worktree);
        if (!WorkspaceExecutionPolicy.CanAdmit(slot, _jobs.Values.Select(j => j.Slot).ToArray(), LimitFor(project.Id), out var reason))
            throw new InvalidOperationException(reason);
        session.Running = true; session.State = "Executando";
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        _jobs.Add(session.Record.Id, (stop, Task.CompletedTask, slot));
        var execution = SendCoreAsync(project, session, "Executar tarefa: " + task.Definition.Title, stop, task.Id);
        _jobs[session.Record.Id] = (stop, execution, slot); RefreshCommands();
        await execution;
    }
    public void CancelTask(WorkspaceTask task) { if (_jobs.TryGetValue(task.ConversationId, out var job)) job.Stop.Cancel(); }
    public bool IsTaskRunning(WorkspaceTask task) => _jobs.ContainsKey(task.ConversationId);

    public bool CanStartTask(WorkspaceProject project, WorkspaceTask task) => !_stopping
        && HasTokenCapacity(project.Id)
        && WorkspaceExecutionPolicy.CanAdmit(TaskSlot(project, task), _jobs.Values.Select(j => j.Slot).ToArray(), LimitFor(project.Id), out _);

    private static WorkspaceExecutionSlot TaskSlot(WorkspaceProject project, WorkspaceTask task) =>
        WorkspaceExecutionSlot.Create(project, new(task.ConversationId, project.Id, task.Definition.Title, task.Definition.Provider,
            task.Definition.Model, null, task.Definition.FunctionName, task.Definition.Instructions, task.Definition.Access, true), task.Worktree);

    public IReadOnlyList<WorkspaceTask> SelectAvailableTasks(WorkspaceProject project, WorkspaceTaskBatch batch)
    {
        if (_stopping || batch.ProjectId != project.Id) return [];
        var slots = _jobs.Values.Select(j => j.Slot).ToList(); var selected = new List<WorkspaceTask>();
        _tokenBudgets.TryGetValue(project.Id, out var budget);
        foreach (var task in batch.Tasks.Where(t => WorkspaceTaskPolicy.CanStart(t, batch.Tasks, AttemptLimitFor(project.Id))))
        {
            if (budget is not null && !budget.CanAdmit(out _)) break;
            var slot = TaskSlot(project, task);
            if (!WorkspaceExecutionPolicy.CanAdmit(slot, slots, LimitFor(project.Id), out _)) continue;
            slots.Add(slot); selected.Add(task);
            if (budget?.Settings.MaxReportedTokens is not null)
                budget = budget with { ReservedTokens = budget.ReservedTokens + budget.Settings.TokenReservation };
        }
        return selected;
    }

    public bool CanPrepareWorktrees => _worktrees is not null && !_stopping;
    public bool CanArchiveWorktrees => _cleanups is not null && !_stopping;
    public bool CanReviewTaskDiffs => _diffs is not null && !_stopping;
    public TaskDiffViewModel CreateTaskDiffReview(WorkspaceProject project, WorkspaceTask task)
    {
        if (!CanReviewTaskDiffs) throw new InvalidOperationException("A revisão de diffs não está disponível ou o aplicativo está encerrando.");
        return new(project, task, _diffs!, _deliveries, _preparations, _validations, _publications);
    }
    public ProjectValidationViewModel CreateProjectValidation() => new(_store, Project ?? throw new InvalidOperationException("Selecione um projeto."));
    public async Task PrepareChiefFollowUpAsync(WorkspaceProject project, WorkspaceTaskBatch batch, string request, CancellationToken token = default)
    {
        if (_stopping || _loadingProject || Project?.Id != project.Id) throw new InvalidOperationException("Selecione este projeto na central antes de preparar o acompanhamento.");
        var revision = _projectRevision; var selectedBefore = SelectedConversation; var draft = Prompt;
        var current = (await _store.GetTaskBatchesAsync(project.Id)).Single(b => b.Id == batch.Id);
        var lastRuns = new Dictionary<string, ChatRun?>();
        foreach (var task in current.Tasks) lastRuns[task.Id] = (await _store.GetRunsAsync(task.ConversationId)).LastOrDefault();
        var prompt = TaskPlanUpdates.BuildChiefPrompt(current, lastRuns, request);
        var combined = string.IsNullOrWhiteSpace(draft) ? prompt : draft + "\n\n" + prompt;
        if (combined.Length > 200_000) throw new InvalidOperationException("O acompanhamento e seu rascunho excedem 200.000 caracteres. Reduza a mensagem; o rascunho foi preservado.");
        var conversations = await _store.GetConversationsAsync(project.Id);
        var chief = selectedBefore?.Record.FunctionName == PlanProposalFormat.ChiefFunctionName && !selectedBefore.Record.IsTask
            ? conversations.Single(c => c.Id == selectedBefore.Record.Id) : null;
        if (chief is null)
        {
            var original = (await _store.GetProposalsAsync(project.Id)).Single(p => p.Id == current.ProposalId);
            foreach (var candidate in conversations.Where(c => c.FunctionName == PlanProposalFormat.ChiefFunctionName && !c.IsTask))
                if ((await _store.GetRunsAsync(candidate.Id)).Any(r => r.Id == original.SourceRunId)) { chief = candidate; break; }
        }
        if (chief is null) throw new InvalidOperationException("Abra uma conversa com a função Chefe do projeto na central antes de preparar o acompanhamento.");
        if (!_sessions.TryGetValue(chief.Id, out var session)) _sessions[chief.Id] = session = new(chief);
        if (session.Running) throw new InvalidOperationException("A chefia ainda está executando. Aguarde antes de preparar outro pedido.");
        session.Record = chief; await LoadConversationAsync(session);
        token.ThrowIfCancellationRequested();
        if (_stopping || revision != _projectRevision || selectedBefore != SelectedConversation || draft != Prompt)
            throw new InvalidOperationException("A central mudou durante a consulta. Seu rascunho foi preservado; prepare o acompanhamento novamente.");
        SelectedConversation = session;
        Prompt = combined;
        Notice = "Estado do plano preparado no chat da chefia. Revise a mensagem e use Enviar; a resposta será uma nova proposta para revisão.";
    }
    public async Task RefreshProjectSessionsAsync(WorkspaceProject project)
    {
        foreach (var record in await _store.GetConversationsAsync(project.Id))
        {
            if (!_sessions.TryGetValue(record.Id, out var session)) _sessions[record.Id] = session = new(record);
            if (!session.Running) session.Record = record;
            if (Project?.Id == project.Id && Conversations.All(c => c.Record.Id != record.Id)) Conversations.Insert(0, session);
        }
    }
    public Task<TaskWorktree> PreviewTaskWorktreeAsync(WorkspaceProject project, WorkspaceTask task, CancellationToken token) =>
        RunGitOperationAsync(stop => _worktrees!.PreviewAsync(project.Id, task.Id, stop), token, _worktrees is not null);
    public Task PrepareTaskWorktreeAsync(WorkspaceProject project, TaskWorktree preview, CancellationToken token) =>
        RunGitOperationAsync(async stop => { await _worktrees!.PrepareAsync(project.Id, preview, stop); return true; }, token, _worktrees is not null);
    public Task<TaskWorktreeCleanupPreview> PreviewTaskArchiveAsync(WorkspaceProject project, WorkspaceTask task, CancellationToken token) =>
        RunGitOperationAsync(stop => _cleanups!.PreviewAsync(project.Id, task.Id, stop), token, _cleanups is not null);
    public Task<TaskWorktreeCleanup> ArchiveTaskWorktreeAsync(WorkspaceProject project, TaskWorktreeCleanupPreview preview, CancellationToken token) =>
        RunGitOperationAsync(stop => _cleanups!.ArchiveAsync(project.Id, preview, stop), token, _cleanups is not null);
    private async Task<T> RunGitOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token, bool available)
    {
        if (!available || _stopping) throw new InvalidOperationException("A operação Git não está disponível ou o aplicativo está encerrando.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, token);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gitJobs.Add(finished.Task);
        try { return await operation(stop.Token); }
        finally { finished.TrySetResult(); _gitJobs.Remove(finished.Task); }
    }

    private async Task<bool> AskPermissionAsync(ConversationPermission permission, CancellationToken token)
    {
        await _permissionGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _dispatcher.InvokeAsync(() => { _permissionAnswer = answer; Permission = permission; });
            return await answer.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { Permission = null; _permissionAnswer = null; });
            _permissionGate.Release();
        }
    }

    private async Task<ConversationQuestionResponse?> AskQuestionsAsync(ConversationQuestionRequest questions, string context, CancellationToken token)
    {
        await _permissionGate.WaitAsync(token).ConfigureAwait(false);
        ConversationQuestionsViewModel? panel = null;
        try
        {
            var answer = new TaskCompletionSource<ConversationQuestionResponse?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await _dispatcher.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                panel = new(questions, context, response => answer.TrySetResult(response));
                Questions = panel;
            });
            return await answer.Task.WaitAsync(token).ConfigureAwait(false);
        }
        finally
        {
            await _dispatcher.InvokeAsync(() => { panel?.Withdraw(); Questions = null; });
            _permissionGate.Release();
        }
    }

    public async Task StopAsync()
    {
        _stopping = true; _lifetime.Cancel(); RefreshCommands();
        var jobs = _jobs.Values.Select(j => j.Task).Concat(_gitJobs).Concat(_catalogQueries.Values).Append(_settingsSave).ToArray();
        try { await Task.WhenAll(jobs); }
        catch (Exception exception) { ShowError(exception); }
        await _tokenRefresh;
    }
    private void ShowError(Exception exception) => Notice = exception is OperationCanceledException ? "Operação cancelada ou prazo excedido." : exception.Message;
    private void RefreshCommands()
    {
        Notify(nameof(ActiveCount)); Notify(nameof(CanConfigure)); Notify(nameof(CanEditFunction)); Notify(nameof(CanEditAccess)); Notify(nameof(CanSend)); Notify(nameof(CanReviewProposals));
        Notify(nameof(CanChooseModel)); Notify(nameof(CanChooseReasoning));
        Notify(nameof(CanManageFunctionProfiles)); Notify(nameof(CanApplyFunctionProfile));
        Notify(nameof(CanEditSessionLimit)); Notify(nameof(SavedSessionLimit)); Notify(nameof(SessionUsage));
        Notify(nameof(TokenBudgetDescription));
        AddProjectCommand?.Refresh(); RefreshModelsCommand?.Refresh(); NewConversationCommand?.Refresh(); SendCommand?.Refresh(); CancelCommand?.Refresh();
        AllowPermissionCommand?.Refresh(); DenyPermissionCommand?.Refresh();
        ApplyFunctionProfileCommand?.Refresh(); RefreshFunctionProfilesCommand?.Refresh();
        SaveSessionLimitCommand?.Refresh();
        RefreshTokenBudgetCommand?.Refresh();
    }
    private static string StateText(ChatRunState? state) => state switch
    {
        ChatRunState.Running => "Executando", ChatRunState.Completed => "Concluída", ChatRunState.Blocked => "Bloqueada",
        ChatRunState.Failed => "Falha", ChatRunState.Cancelled => "Cancelada", ChatRunState.Interrupted => "Interrompida", _ => "Pronta"
    };
}
