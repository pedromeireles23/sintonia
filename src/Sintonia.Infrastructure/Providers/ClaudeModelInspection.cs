using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class ClaudeConversationProvider
{
    public async Task<ProviderCapabilities> InspectAsync(string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); token = deadline.Token;
        await CheckSubscriptionAsync(directory, token).ConfigureAwait(false);
        const string id = "sintonia-models-initialize";
        var initialized = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                    if (response.GetProperty("request_id").GetString() != id) return;
                    if (response.GetProperty("subtype").GetString() == "success")
                        initialized.TrySetResult(response.GetProperty("response").Clone());
                    else initialized.TrySetException(new ProviderException("Claude recusou a consulta de modelos; detalhes internos omitidos."));
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
        await process.WriteLineAsync(JsonSerializer.Serialize(new { type = "control_request", request_id = id, request = new { subtype = "initialize" } }), token).ConfigureAwait(false);
        var winner = await Task.WhenAny(initialized.Task, process.Completion).WaitAsync(token).ConfigureAwait(false);
        if (winner == process.Completion && !initialized.Task.IsCompleted)
            throw new ProviderException("Claude encerrou a consulta de modelos antes de responder.");
        var data = await initialized.Task.WaitAsync(token).ConfigureAwait(false);
        return new(Kind, ReadModels(data), [], ["O CLI não informa nesta consulta o padrão efetivo da instalação nem o reasoning atual. A versão do modelo é confirmada ao conectar a sessão."]);
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
                models.Add(new(model, CodexConversationProvider.MetadataText(entry, "displayName", 120) ?? model, false,
                    CodexConversationProvider.MetadataText(entry, "description", 400) ?? "", null, efforts));
                if (models.Count > 500) throw new ProviderException("Catálogo Claude acima do limite local.");
            }
        if (models.Count == 0)
            models.AddRange([new("opus", "Claude Opus · alias", false), new("sonnet", "Claude Sonnet · alias", false), new("fable", "Claude Fable · alias", false)]);
        return models;
    }
}
