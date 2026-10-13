using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class ClaudeConversationProvider
{
    public async Task<ProviderCapabilities> InspectAsync(string directory, CancellationToken token)
    {
        var data = await RequestControlAsync(directory, "de modelos", null, token).ConfigureAwait(false);
        return new(Kind, ReadModels(data), [], ["Padrão lido das settings do Claude Code (somente model e effortLevel). A versão exata é confirmada ao conectar a sessão."],
            ReadConfiguredDefaults(directory));
    }

    /// <summary>Opens a session without a turn, sends initialize and, optionally, one more control request.
    /// Returns the last reply. No user message is ever written; inbound controls are refused.</summary>
    private async Task<JsonElement> RequestControlAsync(string directory, string label, object? followUp, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); token = deadline.Token;
        await CheckSubscriptionAsync(directory, token).ConfigureAwait(false);
        const string initializeId = "sintonia-models-initialize", followUpId = "sintonia-metadata-request";
        var initialized = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var answered = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderProcess? receiver = null;
        var controls = 0;
        await using var process = new ProviderProcess(Launch,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
             "--permission-prompt-tool", "stdio", "--permission-mode", "manual", "--permission-prompts", "host", "--no-session-persistence"],
            directory, async line =>
            {
                using var document = JsonDocument.Parse(line); var root = document.RootElement;
                if (root.GetProperty("type").GetString() == "control_response")
                {
                    var response = root.GetProperty("response");
                    var target = response.GetProperty("request_id").GetString() switch { initializeId => initialized, followUpId => answered, _ => null };
                    if (target is null) return;
                    if (response.GetProperty("subtype").GetString() == "success")
                        target.TrySetResult(response.GetProperty("response").Clone());
                    else target.TrySetException(new ProviderException($"Claude recusou a consulta {label}; detalhes internos omitidos."));
                }
                else if (root.GetProperty("type").GetString() == "control_request")
                {
                    if (++controls > 16) throw new ProviderException("Claude excedeu o limite de controles da consulta.");
                    var target = receiver ?? throw new ProviderException("Controle recebido antes de conectar a consulta.");
                    var requestId = CodexConversationProvider.MetadataText(root, "request_id", 120)
                        ?? throw new ProviderException("Claude retornou um controle sem identificador válido.");
                    var subtype = root.TryGetProperty("request", out var request)
                        ? CodexConversationProvider.MetadataText(request, "subtype", 40) : null;
                    await target.WriteLineAsync(ClaudeStdioSession.CreateUnsupportedControlResponse(requestId, subtype), token).ConfigureAwait(false);
                }
            });
        receiver = process;
        async Task<JsonElement> RequestAsync(string id, object request, TaskCompletionSource<JsonElement> reply)
        {
            await process.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = id, request }), token).ConfigureAwait(false);
            var winner = await Task.WhenAny(reply.Task, process.Completion).WaitAsync(token).ConfigureAwait(false);
            if (winner == process.Completion && !reply.Task.IsCompleted)
                throw new ProviderException($"Claude encerrou a consulta {label} antes de responder.");
            return await reply.Task.WaitAsync(token).ConfigureAwait(false);
        }
        var data = await RequestAsync(initializeId, new { subtype = "initialize" }, initialized).ConfigureAwait(false);
        return followUp is null ? data : await RequestAsync(followUpId, followUp, answered).ConfigureAwait(false);
    }

    private string ConfigDirectory => configDirectory
        ?? (Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } custom ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"));
    private static readonly string[] ManagedSettingsFiles =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClaudeCode", "managed-settings.json")
    ];

    public ProviderModelDefaults? ReadConfiguredDefaults(string directory) =>
        ReadConfiguredDefaults(directory, ConfigDirectory, ManagedSettingsFiles, Environment.GetEnvironmentVariable);

    /// <summary>Follows Claude Code precedence: ANTHROPIC_MODEL, managed, local project, project and user settings.
    /// Only "model" and "effortLevel" are projected; env, hooks, permissions and other keys are never returned.</summary>
    internal static ProviderModelDefaults? ReadConfiguredDefaults(string directory, string configDirectory,
        IEnumerable<string> managedFiles, Func<string, string?> environment)
    {
        var layers = managedFiles.Select(file => (File: file, Source: "Configuração administrada"))
            .Append((Path.Combine(directory, ".claude", "settings.local.json"), "Configuração local do projeto"))
            .Append((Path.Combine(directory, ".claude", "settings.json"), "Configuração do projeto"))
            .Append((Path.Combine(configDirectory, "settings.json"), "Configuração do usuário"));
        var fromEnvironment = environment("ANTHROPIC_MODEL");
        string? model = string.IsNullOrWhiteSpace(fromEnvironment) || fromEnvironment.Length > 120 || fromEnvironment.Any(char.IsControl) ? null : fromEnvironment;
        string? modelSource = model is null ? null : "Variável de ambiente ANTHROPIC_MODEL", effort = null, effortSource = null;
        foreach (var (file, source) in layers)
        {
            if (model is not null && effort is not null) break;
            var (configuredModel, configuredEffort) = ReadSettingsValues(file);
            if (model is null && configuredModel is not null) (model, modelSource) = (configuredModel, source);
            if (effort is null && configuredEffort is not null) (effort, effortSource) = (configuredEffort, source);
        }
        return model is null && effort is null ? null
            : new(model, effort, modelSource ?? "Padrão do Claude Code", effortSource ?? "Padrão do Claude Code");
    }

    private static (string? Model, string? Effort) ReadSettingsValues(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > 1_000_000) return default;
            using var document = JsonDocument.Parse(File.ReadAllBytes(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var effort = CodexConversationProvider.MetadataText(document.RootElement, "effortLevel", 32);
            try { ModelSettings.ValidateEffort(effort); }
            catch (ArgumentException) { effort = null; }
            return (CodexConversationProvider.MetadataText(document.RootElement, "model", 120), effort);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { return default; }
    }

    internal static IReadOnlyList<ProviderModel> ReadModels(JsonElement data)
    {
        var models = new List<ProviderModel>();
        if (data.TryGetProperty("models", out var entries) && entries.ValueKind == JsonValueKind.Array)
            foreach (var entry in entries.EnumerateArray())
            {
                var model = CodexConversationProvider.MetadataText(entry, "value", 120);
                if (model is null || models.Any(m => m.Id == model)) throw new ProviderException("Catálogo Claude inválido ou com identificador repetido.");
                var efforts = new List<ProviderReasoningEffort>();
                if (entry.TryGetProperty("supportedEffortLevels", out var supported) && supported.ValueKind == JsonValueKind.Array)
                    foreach (var option in supported.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.String) continue;
                        var effort = option.GetString(); ModelSettings.ValidateEffort(effort);
                        if (effort is not null && !efforts.Any(e => e.Id == effort)) efforts.Add(new(effort, ""));
                        if (efforts.Count > 32) throw new ProviderException("Catálogo Claude acima do limite de reasoning.");
                    }
                models.Add(new(model, CodexConversationProvider.MetadataText(entry, "displayName", 120) ?? model, model == "default",
                    CodexConversationProvider.MetadataText(entry, "description", 400) ?? "", null, efforts));
                if (models.Count > 500) throw new ProviderException("Catálogo Claude acima do limite local.");
            }
        if (models.Count == 0)
            models.AddRange([new("opus", "Claude Opus · alias", false), new("sonnet", "Claude Sonnet · alias", false), new("fable", "Claude Fable · alias", false)]);
        return models;
    }
}
