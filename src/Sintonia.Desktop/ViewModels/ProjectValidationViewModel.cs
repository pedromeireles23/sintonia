using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class ValidationCommandDraft : ObservableObject
{
    private string _name = "Novo critério", _executable = "", _arguments = "[]", _directory = ".";
    private int _timeout = 60;
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Executable { get => _executable; set => Set(ref _executable, value); }
    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }
    public string Directory { get => _directory; set => Set(ref _directory, value); }
    public int Timeout { get => _timeout; set => Set(ref _timeout, value); }
    public ProjectValidationCommand ToCommand() => new(Name.Trim(), Executable.Trim(),
        JsonSerializer.Deserialize<string[]>(Arguments) ?? throw new ArgumentException("Use uma lista de argumentos, por exemplo [\"build\", \"projeto com espaços\"]."), Directory.Trim(), Timeout);
    public static ValidationCommandDraft From(ProjectValidationCommand command) => new()
    { Name = command.Name, Executable = command.Executable, Arguments = JsonSerializer.Serialize(command.Arguments), Directory = command.WorkingDirectory, Timeout = command.TimeoutSeconds };
}

public sealed class ProjectValidationViewModel : ObservableObject
{
    private readonly IWorkspaceStore _store;
    private ProjectValidationConfiguration? _saved;
    private ValidationCommandDraft? _selected;
    private bool _busy, _dirty, _stopping;
    private string _notice = "Carregando critérios…";
    private Task _operation = Task.CompletedTask;
    public ProjectValidationViewModel(IWorkspaceStore store, WorkspaceProject project)
    {
        _store = store; Project = project;
        ReloadCommand = new(ReloadAsync, ShowError, () => CanEdit && !IsDirty);
        SaveCommand = new(SaveAsync, ShowError, () => CanEdit && IsDirty);
        AddCommand = new(Add, () => CanEdit && Commands.Count < 10);
        RemoveCommand = new(Remove, () => CanEdit && Selected is not null);
        RevertCommand = new(() => { if (_saved is not null) Load(_saved); }, () => CanEdit && IsDirty);
        MoveUpCommand = new(() => Move(-1), () => CanEdit && Selected is not null && Commands.IndexOf(Selected) > 0);
        MoveDownCommand = new(() => Move(1), () => CanEdit && Selected is not null && Commands.IndexOf(Selected) < Commands.Count - 1);
    }
    public WorkspaceProject Project { get; }
    public string Context => $"{Project.Name}\n{Project.Directory}";
    public ObservableCollection<ValidationCommandDraft> Commands { get; } = [];
    public ValidationCommandDraft? Selected { get => _selected; set { Set(ref _selected, value); Refresh(); } }
    public bool Busy { get => _busy; private set { Set(ref _busy, value); Refresh(); } }
    public bool IsDirty { get => _dirty; private set { Set(ref _dirty, value); Refresh(); } }
    public bool CanEdit => !Busy && !_stopping;
    public string State => $"Revisão salva: {_saved?.Revision ?? 0} · {Commands.Count}/10 comandos" + (IsDirty ? " · Alterações não salvas" : "");
    public string Notice { get => _notice; private set => Set(ref _notice, value); }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand SaveCommand { get; }
    public DelegateCommand AddCommand { get; }
    public DelegateCommand RemoveCommand { get; }
    public DelegateCommand RevertCommand { get; }
    public DelegateCommand MoveUpCommand { get; }
    public DelegateCommand MoveDownCommand { get; }
    public Task ReloadAsync() => !CanEdit || IsDirty ? Task.CompletedTask : _operation = ReloadCoreAsync();
    private async Task ReloadCoreAsync()
    {
        Busy = true; await Task.Yield();
        try { Load(await _store.GetProjectValidationAsync(Project.Id)); Notice = "Comandos executam na ordem da lista, na pasta combinada. Salvar não inicia processos."; }
        catch (Exception error) { ShowError(error); }
        finally { Busy = false; }
    }
    private void Load(ProjectValidationConfiguration configuration)
    {
        foreach (var draft in Commands) draft.PropertyChanged -= Changed;
        Commands.Clear(); _saved = configuration;
        foreach (var command in configuration.Commands) { var draft = ValidationCommandDraft.From(command); draft.PropertyChanged += Changed; Commands.Add(draft); }
        Selected = Commands.FirstOrDefault(); IsDirty = false; Refresh();
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { IsDirty = true; Refresh(); }
    private void Add()
    {
        if (!CanEdit || Commands.Count >= 10) return;
        var draft = new ValidationCommandDraft(); draft.PropertyChanged += Changed; Commands.Add(draft); Selected = draft; IsDirty = true;
    }
    private void Remove()
    {
        if (!CanEdit || Selected is not { } selected) return;
        selected.PropertyChanged -= Changed; Commands.Remove(selected); Selected = Commands.FirstOrDefault(); IsDirty = true;
    }
    private void Move(int offset)
    {
        if (!CanEdit || Selected is null) return;
        var index = Commands.IndexOf(Selected); var target = index + offset;
        if (target < 0 || target >= Commands.Count) return;
        Commands.Move(index, target); IsDirty = true;
    }
    public Task SaveAsync() => !CanEdit || !IsDirty ? Task.CompletedTask : _operation = SaveCoreAsync();
    private async Task SaveCoreAsync()
    {
        Busy = true; await Task.Yield();
        try
        {
            var draft = new ProjectValidationConfiguration(Project.Id, _saved?.Revision ?? 0, Commands.Select(c => c.ToCommand()).ToArray());
            draft.ValidateDefinition(); Load(await _store.SaveProjectValidationAsync(draft));
            Notice = "Critérios salvos. Abra Validar combinação nos diffs para conferir e iniciar os comandos.";
        }
        catch (Exception error) { ShowError(error); }
        finally { Busy = false; }
    }
    private void ShowError(Exception error) => Notice = error.Message;
    public async Task StopAsync() { _stopping = true; Refresh(); await _operation; }
    private void Refresh()
    {
        Notify(nameof(CanEdit)); Notify(nameof(State));
        ReloadCommand?.Refresh(); SaveCommand?.Refresh(); AddCommand?.Refresh(); RemoveCommand?.Refresh(); RevertCommand?.Refresh(); MoveUpCommand?.Refresh(); MoveDownCommand?.Refresh();
    }
}
