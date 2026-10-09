using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public static class RunTokenUsageParser
{
    public static RunTokenUsage? Codex(JsonElement data)
    {
        try
        {
            var usage = new RunTokenUsage(ProviderKind.Codex, Required(data, "inputTokens"), Required(data, "outputTokens"), Required(data, "totalTokens"),
                Required(data, "cachedInputTokens"), Optional(data, "cacheWriteInputTokens"), Required(data, "reasoningOutputTokens"));
            usage.ValidateDefinition(); return usage;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException) { return null; }
    }
    public static RunTokenUsage? ClaudeResult(JsonElement root)
    {
        try
        {
            if (!root.TryGetProperty("usage", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            // In streaming input mode result.usage belongs to this main-loop turn. modelUsage and USD can include resumed history/subagents.
            var read = Required(data, "cache_read_input_tokens"); var write = Required(data, "cache_creation_input_tokens");
            var input = checked(Required(data, "input_tokens") + read + write); var output = Required(data, "output_tokens");
            var success = root.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String && subtype.GetString() == "success"
                && root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.False;
            var usage = new RunTokenUsage(ProviderKind.Claude, input, output, checked(input + output), read, write, IsPartial: !success);
            usage.ValidateDefinition(); return usage;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or InvalidOperationException or OverflowException) { return null; }
    }
    private static long Required(JsonElement data, string field) => Optional(data, field) ?? throw new FormatException();
    private static long? Optional(JsonElement data, string field)
    {
        if (data.ValueKind != JsonValueKind.Object) throw new FormatException();
        if (!data.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0) throw new FormatException();
        return count;
    }
}

/// <summary>Only same-turn positive changes in valid cumulative counters; no last-request summation or inferred resumed baseline.</summary>
internal sealed class CodexRunTokenTracker(bool newThread, IProgress<ConversationEvent> progress)
{
    private readonly object _gate = new();
    private readonly Queue<(string Turn, RunTokenUsage Total)> _pending = new();
    private RunTokenUsage? _previous = newThread ? new(ProviderKind.Codex, 0, 0, 0, 0, 0, 0) : null;
    private RunTokenUsage? _observed;
    private string? _turn;
    private bool _armed, _closed;
    public RunTokenUsage? Snapshot { get { lock (_gate) return _observed; } }
    public void Arm() { lock (_gate) _armed = true; }
    public void SetTurn(string turn)
    {
        lock (_gate)
        {
            _turn = turn;
            while (_pending.TryDequeue(out var update)) if (update.Turn == _turn) Observe(update.Total);
        }
    }
    public void Accept(JsonElement data)
    {
        lock (_gate)
        {
            if (_closed || !data.TryGetProperty("tokenUsage", out var usage) || usage.ValueKind != JsonValueKind.Object
                || !usage.TryGetProperty("total", out var total) || !data.TryGetProperty("turnId", out var turn) || turn.ValueKind != JsonValueKind.String) return;
            var parsed = RunTokenUsageParser.Codex(total); var id = turn.GetString();
            if (parsed is null || string.IsNullOrWhiteSpace(id) || id.Length > 200) return;
            if (!_armed) { _previous = parsed; return; }
            if (_turn is null)
            {
                // Bound racing notifications before turn/start's response. On overflow, avoid attributing the dropped interval.
                if (_pending.Count == 64) { _pending.Clear(); _previous = null; }
                _pending.Enqueue((id, parsed));
            }
            else if (id == _turn) Observe(parsed);
        }
    }
    public void Close() { lock (_gate) { _closed = true; _pending.Clear(); } }
    private void Observe(RunTokenUsage current)
    {
        var previous = _previous; _previous = current;
        if (previous is null) return; // First resumed sample becomes a baseline, never an invented zero.
        if (current.InputTokens < previous.InputTokens || current.OutputTokens < previous.OutputTokens
            || current.CacheReadTokens < previous.CacheReadTokens || current.CacheWriteTokens < previous.CacheWriteTokens
            || current.ReasoningOutputTokens < previous.ReasoningOutputTokens) return; // A reset starts another observed interval.
        try
        {
            var input = checked((_observed?.InputTokens ?? 0) + (current.InputTokens - previous.InputTokens));
            var output = checked((_observed?.OutputTokens ?? 0) + (current.OutputTokens - previous.OutputTokens));
            var observed = new RunTokenUsage(ProviderKind.Codex, input, output, checked(input + output),
                AddOptional(_observed?.CacheReadTokens, current.CacheReadTokens, previous.CacheReadTokens),
                AddOptional(_observed?.CacheWriteTokens, current.CacheWriteTokens, previous.CacheWriteTokens),
                AddOptional(_observed?.ReasoningOutputTokens, current.ReasoningOutputTokens, previous.ReasoningOutputTokens));
            observed.ValidateDefinition();
            if (observed == _observed) return;
            _observed = observed; progress.Report(new(ConversationEventKind.TokenUsage, observed.Describe(), TokenUsage: observed));
        }
        catch (Exception error) when (error is ArgumentException or OverflowException) { }
    }
    private static long? AddOptional(long? accumulated, long? current, long? previous) => current is null || previous is null ? null
        : checked((accumulated ?? 0) + current.Value - previous.Value);
}
