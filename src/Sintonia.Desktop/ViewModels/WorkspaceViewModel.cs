using System.Collections.ObjectModel;
using System.Windows.Threading;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed record FunctionOption(string Name, string Instructions);
public sealed record AccessOption(string Name, ConversationAccess Value);

public sealed class ChatMessageViewModel(string author, string text, string state = "") : ObservableObject
{
    private string _text = text;
    private string _state = state;
    public string Author => author;
    public string Text { get => _text; set => Set(ref _text, value); }
    public string State { get => _state; set => Set(ref _state, value); }
}

public sealed class ConversationViewModel(WorkspaceConversation record) : ObservableObject
{
    private WorkspaceConversation _record = record;
    private string _state = "Pronta";
    private bool _running;
    public WorkspaceConversation Record { get => _record; set { _record = value; Notify(); Notify(nameof(Title)); Notify(nameof(Description)); } }
    public string Title => Record.Title;
    public string Description => $"{Record.Provider} · {Record.Model ?? "Padrão"} · {Record.FunctionName}" + (Record.IsTask ? " · Tarefa da fila" : "");
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
    private readonly HashSet<Task> _gitJobs = [];
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _pickDirectory;
    private readonly Func<ProviderKind, string, CancellationToken, Task<ProviderCapabilities>> _inspect;
    private readonly Dictionary<string, ConversationViewModel> _sessions = [];
    private readonly Dictionary<string, (CancellationTokenSource Stop, Task Task)> _jobs = [];
    private readonly SemaphoreSlim _permissionGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private TaskCompletionSource<bool>? _permissionAnswer;
    private ConversationPermission? _permission;
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
    private long _catalogRevision;

    public WorkspaceViewModel(IWorkspaceStore store, WorkspaceChatService chat, Dispatcher dispatcher,
        Func<string?> pickDirectory, Func<ProviderKind, string, CancellationToken, Task<ProviderCapabilities>> inspect,
        TaskWorktreeService? worktrees = null)
    {
        _store = store; _chat = chat; _dispatcher = dispatcher; _pickDirectory = pickDirectory; _inspect = inspect;
        _worktrees = worktrees;
        _function = Functions[0]; _access = AccessOptions[0];
        AddProjectCommand = new(AddPickedProjectAsync, ShowError, () => Ready && !_stopping);
        RefreshModelsCommand = new(RefreshModelsAsync, ShowError, () => Ready && Project is not null && CanConfigure);
        NewConversationCommand = new(NewConversation, () => Ready && Project is not null && !_stopping);
        SendCommand = new(() => StartSend(), () => CanSend);
        CancelCommand = new(() => { if (SelectedConversation is { } current && _jobs.TryGetValue(current.Record.Id, out var job)) job.Stop.Cancel(); }, () => SelectedConversation?.Running == true);
        AllowPermissionCommand = new(() => _permissionAnswer?.TrySetResult(true), () => Permission is not null);
        DenyPermissionCommand = new(() => _permissionAnswer?.TrySetResult(false), () => Permission is not null);
        ApplyFunctionProfileCommand = new(ApplyFunctionProfileAsync, ShowError, () => CanApplyFunctionProfile);
        RefreshFunctionProfilesCommand = new(RefreshFunctionProfilesAsync, ShowError, () => CanManageFunctionProfiles);
    }

    public ObservableCollection<WorkspaceProject> Projects { get; } = [];
    public ObservableCollection<ConversationViewModel> Conversations { get; } = [];
    public ObservableCollection<string> Models { get; } = [DefaultModel];
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
            if (!Set(ref _selected, value)) return;
            Prompt = "";
            if (value is not null)
            {
                _provider = value.Record.Provider; _model = value.Record.Model ?? DefaultModel;
                _instructions = value.Record.Instructions;
                _function = FindFunction(value.Record.FunctionName);
                _access = AccessOptions.Single(a => a.Value == value.Record.Access);
                Notify(nameof(Provider)); Notify(nameof(Model)); Notify(nameof(Instructions)); Notify(nameof(Function)); Notify(nameof(Access));
                _ = LoadConversationAsync(value);
            }
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
            ResetModels();
        }
    }
    public string Model { get => _model; set => Set(ref _model, value); }
    public string Prompt { get => _prompt; set { if (Set(ref _prompt, value)) RefreshCommands(); } }
    public string Instructions { get => _instructions; set => Set(ref _instructions, value); }
    public FunctionOption Function { get => _function; set { if (Set(ref _function, value)) { Instructions = value.Instructions; if (IsChief) Access = AccessOptions[0]; Notify(nameof(CanEditAccess)); } } }
    public bool IsChief => Function.Name == PlanProposalFormat.ChiefFunctionName;
    public bool CanEditAccess => CanEditFunction && !IsChief;
    public AccessOption Access { get => _access; set { Set(ref _access, IsChief ? AccessOptions[0] : value); RefreshCommands(); } }
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public bool Ready { get => _ready; private set { Set(ref _ready, value); RefreshCommands(); } }
    public int ActiveCount => _jobs.Count;
    public bool CanConfigure => SelectedConversation is null && !_stopping && !_loadingProject;
    public bool CanReviewProposals => Ready && Project is not null && !_stopping;
    public bool CanManageFunctionProfiles => Ready && !_stopping;
    public bool CanApplyFunctionProfile => CanManageFunctionProfiles && Project is not null && !_loadingProject && SelectedFunctionProfile is not null;
    public bool CanEditFunction => SelectedConversation?.Running != true && SelectedConversation?.Record.IsTask != true && !_stopping && !_loadingProject;
    public bool CanSend => Ready && Project is not null && !_stopping && !_loadingProject && SelectedConversation?.Running != true
        && SelectedConversation?.Record.IsTask != true
        && (SelectedConversation is null || SelectedConversation.Loaded) && _jobs.Count < 2
        && !_jobs.Keys.Any(id => _sessions[id].Record.Provider == Provider)
        && !_jobs.Keys.Any(id => _sessions[id].Record.ProjectId == Project.Id && (Access.Value == ConversationAccess.WorkspaceWrite || _sessions[id].Record.Access == ConversationAccess.WorkspaceWrite))
        && !string.IsNullOrWhiteSpace(Prompt);
    public ConversationPermission? Permission { get => _permission; private set { Set(ref _permission, value); Notify(nameof(HasPermission)); RefreshCommands(); } }
    public bool HasPermission => Permission is not null;
    public AsyncCommand AddProjectCommand { get; }
    public AsyncCommand RefreshModelsCommand { get; }
    public DelegateCommand NewConversationCommand { get; }
    public DelegateCommand SendCommand { get; }
    public DelegateCommand CancelCommand { get; }
    public DelegateCommand AllowPermissionCommand { get; }
    public DelegateCommand DenyPermissionCommand { get; }
    public AsyncCommand ApplyFunctionProfileCommand { get; }
    public AsyncCommand RefreshFunctionProfilesCommand { get; }

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
            ResetModels();
        }
        catch (Exception exception) { ShowError(exception); }
        finally { if (revision == _projectRevision) { _loadingProject = false; RefreshCommands(); } }
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
                session.Messages.Add(new(session.Record.Provider.ToString(), run.Response ?? "", StateText(run.State)));
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
        SelectedConversation = null; Prompt = ""; Model = DefaultModel; ResetModels();
        Notice = "Escolha a IA e o modelo. Uma sessão nova será criada ao enviar.";
    }
    private void ResetModels()
    {
        Models.Clear(); Models.Add(DefaultModel);
        if (Provider == ProviderKind.Claude) { Models.Add("opus"); Models.Add("sonnet"); Models.Add("fable"); }
        if (Model != DefaultModel && !Models.Contains(Model)) Models.Add(Model);
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
        Prompt = prompt; ResetModels();
        Notice = $"Perfil {current.Name} aplicado à nova conversa em leitura. Confira o modelo e as instruções antes de enviar.";
    }
    public async Task<FunctionProfileLibraryViewModel> LoadFunctionProfileLibraryAsync(Func<WorkspaceFunctionProfile, bool> confirmDelete)
    {
        var library = new FunctionProfileLibraryViewModel(_store, RefreshFunctionProfilesAsync, confirmDelete);
        await library.InitializeAsync(); return library;
    }
    private async Task RefreshModelsAsync()
    {
        var revision = ++_catalogRevision;
        var provider = Provider;
        var project = Project!;
        Notice = "Consultando modelos e extensões da instalação; nenhuma mensagem será enviada ao modelo.";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var capabilities = await _inspect(provider, project.Directory, timeout.Token);
        if (revision != _catalogRevision || Provider != provider || Project?.Id != project.Id || !CanConfigure) return;
        ResetModels();
        foreach (var model in capabilities.Models) if (!Models.Contains(model.Id)) Models.Add(model.Id);
        Notice = capabilities.Warnings.Count > 0 ? string.Join(" ", capabilities.Warnings) : $"{capabilities.Models.Count} opções de modelo. O provedor confirma o acesso da assinatura ao enviar.";
    }

    private void StartSend()
    {
        if (!CanSend) return;
        var prompt = Prompt; Prompt = "";
        var project = Project!;
        var session = SelectedConversation;
        if (session is null)
        {
            var title = prompt.Split('\n')[0].Trim();
            if (title.Length > 52) title = title[..52] + "…";
            session = new(new(Guid.NewGuid().ToString(), project.Id, title, Provider, Model == DefaultModel || string.IsNullOrWhiteSpace(Model) ? null : Model,
                null, Function.Name, Instructions, Access.Value)) { Loaded = true };
            _sessions.Add(session.Record.Id, session); Conversations.Insert(0, session); SelectedConversation = session;
        }
        else session.Record = session.Record with { FunctionName = Function.Name, Instructions = Instructions, Access = Access.Value };
        session.Running = true; session.State = "Executando";
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        // Reserve the view immediately so rapid clicks cannot launch the same conversation twice.
        _jobs.Add(session.Record.Id, (stop, Task.CompletedTask));
        var task = SendCoreAsync(project, session, prompt, stop);
        _jobs[session.Record.Id] = (stop, task);
        RefreshCommands();
    }

    private async Task SendCoreAsync(WorkspaceProject project, ConversationViewModel session, string prompt, CancellationTokenSource stop, string? taskId = null)
    {
        await Task.Yield();
        var isChief = taskId is null && session.Record.FunctionName == PlanProposalFormat.ChiefFunctionName;
        var answer = new ChatMessageViewModel(session.Record.Provider.ToString(), "", "Executando");
        session.Messages.Add(new("Você", prompt)); session.Messages.Add(answer);
        var progress = new Progress<ConversationEvent>(ev =>
        {
            if (ev.Kind == ConversationEventKind.Session)
            {
                session.Record = session.Record with { NativeSessionId = ev.NativeSessionId, Model = ev.Model };
                if (SelectedConversation == session) { _model = ev.Model ?? DefaultModel; Notify(nameof(Model)); }
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
            var run = taskId is null ? await _chat.SendAsync(project, session.Record, prompt, progress, stop.Token, AskPermissionAsync)
                : await _chat.SendTaskAsync(project.Id, taskId, progress, stop.Token, AskPermissionAsync);
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
        catch (Exception exception)
        {
            answer.State = session.State = "Falha";
            session.Messages.Add(new("Sintonia", exception.Message)); ShowError(exception);
        }
        finally
        {
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

    public async Task StartTaskAsync(WorkspaceProject project, WorkspaceTask task)
    {
        if (_stopping) throw new InvalidOperationException("O aplicativo está encerrando.");
        var session = await GetTaskConversationAsync(project, task);
        if (_stopping || _jobs.ContainsKey(session.Record.Id)) throw new InvalidOperationException("A conversa já está executando ou o aplicativo está encerrando.");
        session.Running = true; session.State = "Executando";
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _jobs.Add(session.Record.Id, (stop, Task.CompletedTask));
        var execution = SendCoreAsync(project, session, "Executar tarefa: " + task.Definition.Title, stop, task.Id);
        _jobs[session.Record.Id] = (stop, execution); RefreshCommands();
        await execution;
    }
    public void CancelTask(WorkspaceTask task) { if (_jobs.TryGetValue(task.ConversationId, out var job)) job.Stop.Cancel(); }

    public bool CanPrepareWorktrees => _worktrees is not null && !_stopping;
    public Task<TaskWorktree> PreviewTaskWorktreeAsync(WorkspaceProject project, WorkspaceTask task, CancellationToken token) =>
        RunGitOperationAsync(stop => _worktrees!.PreviewAsync(project.Id, task.Id, stop), token);
    public Task PrepareTaskWorktreeAsync(WorkspaceProject project, TaskWorktree preview, CancellationToken token) =>
        RunGitOperationAsync(async stop => { await _worktrees!.PrepareAsync(project.Id, preview, stop); return true; }, token);
    private async Task<T> RunGitOperationAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        if (!CanPrepareWorktrees) throw new InvalidOperationException("A preparação não está disponível ou o aplicativo está encerrando.");
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

    public async Task StopAsync()
    {
        _stopping = true; _lifetime.Cancel(); RefreshCommands();
        var jobs = _jobs.Values.Select(j => j.Task).Concat(_gitJobs).ToArray();
        await Task.WhenAll(jobs);
    }
    private void ShowError(Exception exception) => Notice = exception is OperationCanceledException ? "Operação cancelada ou prazo excedido." : exception.Message;
    private void RefreshCommands()
    {
        Notify(nameof(ActiveCount)); Notify(nameof(CanConfigure)); Notify(nameof(CanEditFunction)); Notify(nameof(CanEditAccess)); Notify(nameof(CanSend)); Notify(nameof(CanReviewProposals));
        Notify(nameof(CanManageFunctionProfiles)); Notify(nameof(CanApplyFunctionProfile));
        AddProjectCommand?.Refresh(); RefreshModelsCommand?.Refresh(); NewConversationCommand?.Refresh(); SendCommand?.Refresh(); CancelCommand?.Refresh();
        AllowPermissionCommand?.Refresh(); DenyPermissionCommand?.Refresh();
        ApplyFunctionProfileCommand?.Refresh(); RefreshFunctionProfilesCommand?.Refresh();
    }
    private static string StateText(ChatRunState? state) => state switch
    {
        ChatRunState.Running => "Executando", ChatRunState.Completed => "Concluída", ChatRunState.Blocked => "Bloqueada",
        ChatRunState.Failed => "Falha", ChatRunState.Cancelled => "Cancelada", ChatRunState.Interrupted => "Interrompida", _ => "Pronta"
    };
}
