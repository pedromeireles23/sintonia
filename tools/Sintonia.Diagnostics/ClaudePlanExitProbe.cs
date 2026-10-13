using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudePlanExitProbe
{
    internal const string Tool = "ExitPlanMode";
    internal const string EvidenceFile = "plan-exit-evidence.json";
    internal const string HostDiagnostic = "Saída do planejamento Claude recusada:";
    internal const string Denial = "Permissão recusada: " + Tool;
    internal const string Prompt = "Prova finita de recusa do Sintonia, em leitura. A proposta desta prova é somente encerrar sem implementar alterações. "
        + "Use exatamente uma vez ExitPlanMode com entrada vazia {}. O host deve recusar essa saída; não tente mudar o modo de permissão. "
        + "Não use outras ferramentas, arquivos, comandos, perguntas ou agentes. Não escreva um plano em arquivo. "
        + "Depois da negativa, encerre informando que a ação foi recusada. Não repita, não contorne e não execute a proposta.";
    internal sealed record ResultEvidence(string ToolUseId, string ToolName, bool? IsError, int TextLength,
        string TextHash, bool Truncated, bool HostRefusalFound);
    internal sealed record Evidence(string Version, string State, int Attempts, int ToolCalls, int HostRefusals,
        int Denials, int Permissions, int Questions, string? Outcome, IReadOnlyList<string> PermissionDenials,
        string? SessionId, string? Model, RunTokenUsage? TokenUsage, IReadOnlyList<ResultEvidence> Results, string? FailureCode);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Saída planejamento Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        Console.WriteLine("Prova real ExitPlanMode: um envio, 90s/duas iterações internas, leitura e recusa obrigatória; sem retry.");
        Console.WriteLine("Pasta exclusiva e evidência parcial: " + root);
        var native = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var launch = CreateLaunch(native);
        try
        {
            var version = await ProcessProbe.RunAsync(native, ["--version"], root, TimeSpan.FromSeconds(10), token);
            if (version.ExitCode != 0 || version.TimedOut || version.Truncated || version.StandardOutput.Trim().Length is < 1 or > 100)
                throw new ProviderException("Não foi possível conferir a versão Claude.");
            await new ClaudeConversationProvider(launch).CheckSubscriptionAsync(root, token);
            await ClaudeQuestionProbe.CheckReadyAsync(launch, root, token); // Initialize only; no user message.
            await RunCaseAsync(root, observe => new ClaudeConversationProvider(launch, observe), version.StandardOutput.Trim(), token);
            Verify(root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new ProviderException("Prova de recusa ExitPlanMode não concluída; pasta e eventual evidência parcial preservadas, sem reenvio. Detalhes internos omitidos.");
        }
        Console.WriteLine("PASS: uma ExitPlanMode real recusada pelo host, resultado de erro correlacionado e execução Blocked; nenhum callback de aprovação/pergunta ou escrita na pasta.");
    }

    internal static ExecutableLaunch CreateLaunch(ExecutableLaunch native) => native with
    {
        PrefixArguments = native.PrefixArguments.Concat(new[]
        {
            "--settings", JsonSerializer.Serialize(new { permissions = new { ask = new[] { Tool } } }),
            "--mcp-config", "{\"mcpServers\":{}}", "--strict-mcp-config", "--tools", Tool, "--max-turns", "2"
        }).ToArray()
    };

    internal static async Task RunCaseAsync(string root, Func<Action<ClaudeToolResult>, IConversationProvider> createProvider,
        string version, CancellationToken token)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new ProviderException("Pasta da prova já usada; não reenviar turnos.");
        var attempts = 0; var calls = 0; var hostRefusals = 0; var denials = 0; var permissions = 0; var questions = 0;
        ConversationResult? returned = null; string? failure = null; string? session = null; string? model = null; RunTokenUsage? usage = null;
        var results = new ConcurrentQueue<ResultEvidence>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await Save("Prepared");
        try
        {
            var provider = createProvider(result => results.Enqueue(new(result.ToolUseId, result.ToolName, result.IsError,
                result.Text.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text))), result.Truncated,
                result.Text.Contains("recusada pelo Sintonia", StringComparison.OrdinalIgnoreCase))));
            attempts = 1; await Save("Sending");
            returned = await provider.SendAsync(new(root, Prompt, Access: ConversationAccess.ReadOnly,
                PermissionHandler: (_, _) => { Interlocked.Increment(ref permissions); deadline.Cancel(); return Task.FromResult(false); },
                QuestionHandler: (_, _) => { Interlocked.Increment(ref questions); deadline.Cancel(); return Task.FromResult<ConversationQuestionResponse?>(null); }),
                new ProgressEvents(Report), deadline.Token);
            Check(Snapshot("Completed"));
            if (deadline.IsCancellationRequested || Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) != EvidenceFile))
                throw new ProviderException("Cancelamento ou arquivo inesperado na prova de recusa.");
            await Save("Completed");
        }
        catch (Exception exception)
        {
            failure = exception is ProviderException { FailureKind: ProviderFailureKind.AuthenticationRefreshFailed } ? "OAuthRefreshFailed"
                : exception is OperationCanceledException || deadline.IsCancellationRequested ? "Cancelled" : "ProofFailed";
            await Save("Interrupted"); throw;
        }

        Evidence Snapshot(string state) => new(version, state, attempts, calls, hostRefusals, denials, permissions, questions,
            returned?.Outcome.ToString(), returned?.PermissionDenials ?? [], returned?.NativeSessionId ?? session, returned?.Model ?? model,
            returned?.TokenUsage ?? usage, results.ToArray(), failure);
        Task Save(string state) => File.WriteAllTextAsync(Path.Combine(root, EvidenceFile),
            JsonSerializer.Serialize(Snapshot(state), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);
        void Report(ConversationEvent ev)
        {
            if (ev.Kind == ConversationEventKind.Session) { session = ev.NativeSessionId; model = ev.Model; }
            if (ev.Kind == ConversationEventKind.TokenUsage) usage = ev.TokenUsage;
            if (ev.Kind == ConversationEventKind.Tool && (Interlocked.Increment(ref calls) != 1 || ev.Text != Tool)) deadline.Cancel();
            if (ev.Kind == ConversationEventKind.Diagnostic && ev.Text.StartsWith(HostDiagnostic, StringComparison.Ordinal))
                if (Interlocked.Increment(ref hostRefusals) != 1) deadline.Cancel();
            if (ev.Kind == ConversationEventKind.PermissionDenied && (Interlocked.Increment(ref denials) != 1 || ev.Text != Denial)) deadline.Cancel();
        }
    }

    public static void Verify(string root) => Check(JsonSerializer.Deserialize<Evidence>(File.ReadAllText(Path.Combine(root, EvidenceFile)))
        ?? throw new ProviderException("Evidência de recusa ausente."));
    private static void Check(Evidence evidence)
    {
        if (string.IsNullOrWhiteSpace(evidence.Version) || evidence.State != "Completed" || evidence.FailureCode is not null
            || evidence.Attempts != 1 || evidence.ToolCalls != 1 || evidence.HostRefusals != 1 || evidence.Denials != 1
            || evidence.Permissions != 0 || evidence.Questions != 0 || evidence.Outcome != "Blocked"
            || evidence.PermissionDenials is null || evidence.PermissionDenials.Count != 1 || evidence.PermissionDenials[0] != Tool
            || string.IsNullOrWhiteSpace(evidence.SessionId) || string.IsNullOrWhiteSpace(evidence.Model)
            || evidence.Results is null || evidence.Results.Count != 1
            || evidence.Results.Any(result => result.ToolName != Tool || string.IsNullOrWhiteSpace(result.ToolUseId)
                || result.IsError != true || !result.HostRefusalFound || result.Truncated || result.TextLength <= 0
                || result.TextHash.Length != 64 || !result.TextHash.All(Uri.IsHexDigit)))
            throw new ProviderException("Evidência não confirma uma recusa ExitPlanMode real e execução bloqueada; não repetir chamadas.");
    }
    private sealed class ProgressEvents(Action<ConversationEvent> report) : IProgress<ConversationEvent>
    {
        public void Report(ConversationEvent value) => report(value);
    }
}
