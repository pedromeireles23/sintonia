using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudeToolPermissionProbe
{
    internal sealed record ActionSpec(string Tool, string Directory, string FileName, string Marker, string Original)
    {
        public string FilePath => Path.Combine(Directory, FileName);
        public string Command => $"rtk proxy python -c \"from pathlib import Path; Path('{FileName}').write_text('{Marker}', encoding='utf-8')\"";
        public object Input => Tool == "Bash" ? new { command = Command, timeout = 10000, run_in_background = false } : (object)new
            { file_path = FilePath, old_string = Original, new_string = Marker, replace_all = false };
        public string Prompt => "Prova finita de autorização. " + (Tool == "Edit"
            ? $"Leia somente {FileName} uma vez com Read; em seguida use Edit exatamente uma vez. "
            : "Use Bash exatamente uma vez, sem mudar o comando. ")
            + $"Use estes parâmetros exatos para {Tool}:\n{JsonSerializer.Serialize(Input)}\n"
            + "Não use outras ferramentas, MCPs, rede ou outros agentes. Não execute em background ou desative isolamento. "
            + "Se houver recusa, pare sem repetir nem contornar. Ao terminar, responda em uma frase curta.";
    }

    internal sealed record StepEvidence(string Step, int Requests, int MatchingRequests, int Approvals,
        string Outcome, string SessionId, string Model, RunTokenUsage? TokenUsage,
        IReadOnlyDictionary<string, string?> FileHashes);
    internal sealed record CaseEvidence(string Tool, string Marker, string Original, string State,
        IReadOnlyList<StepEvidence> Steps);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Bash Edit Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        Console.WriteLine("Prova real finita Claude: quatro turnos no máximo, 90s por turno, três iterações por turno; sem retries.");
        Console.WriteLine("Pasta exclusiva e evidências parciais: " + root);
        var launch = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var settings = JsonSerializer.Serialize(new { permissions = new { ask = new[] { "Write", "Edit", "Bash", "PowerShell" } } });
        // Fixed two cases, not a retry loop. Preserve native customization; narrow tools for this proof only.
        foreach (var tool in new[] { "Bash", "Edit" })
        {
            var provider = new ClaudeConversationProvider(launch with { PrefixArguments = launch.PrefixArguments.Concat(new[]
            {
                // End with a scalar option: variadic tool flags would otherwise consume auth/status arguments.
                "--settings", settings, "--tools", tool == "Bash" ? "Bash" : "Read,Edit",
                "--disallowedTools", "mcp__*", "--max-turns", "3"
            }).ToArray() });
            await RunCaseAsync(root, tool, provider, token).ConfigureAwait(false);
        }
        VerifyFiles(root);
        Console.WriteLine("PASS: Bash/Edit recusados sem efeitos e autorizados uma vez com conteúdo exato; retomada por ferramenta comprovada.");
    }

    internal static async Task RunCaseAsync(string root, string tool, IConversationProvider provider, CancellationToken token)
    {
        if (tool is not ("Bash" or "Edit")) throw new ArgumentException("Ferramenta de prova inválida.", nameof(tool));
        var directory = Path.Combine(root, tool);
        // Never reuse a directory that could already contain effects from an earlier attempt.
        if (Directory.Exists(directory)) throw new ProviderException("Pasta da prova já existe; não repetir ações.");
        Directory.CreateDirectory(directory);
        var marker = "SINTONIA-AUTHORIZED-" + Guid.NewGuid().ToString("N");
        var original = "SINTONIA-ORIGINAL-" + Guid.NewGuid().ToString("N");
        var denied = new ActionSpec(tool, directory, "negado.txt", marker, original);
        var allowed = new ActionSpec(tool, directory, "permitido.txt", marker, original);
        if (tool == "Edit")
        {
            await File.WriteAllTextAsync(denied.FilePath, original, new UTF8Encoding(false), token);
            await File.WriteAllTextAsync(allowed.FilePath, original, new UTF8Encoding(false), token);
        }
        var steps = new List<StepEvidence>();
        var evidencePath = Path.Combine(root, tool + "-evidence.json");
        await SaveAsync("Prepared");
        try
        {
            var first = await SendStepAsync(denied, false, null).ConfigureAwait(false);
            CheckFiles(directory, tool, original, marker, allowed: false);
            await SaveAsync("DeniedVerified");
            await SendStepAsync(allowed, true, first).ConfigureAwait(false);
            CheckFiles(directory, tool, original, marker, allowed: true);
            await SaveAsync("Completed");
            Console.WriteLine($"PASS {tool}: recusa e autorização conferidas, dois turnos; modelo {steps[^1].Model}.");
        }
        catch
        {
            await SaveAsync("Interrupted");
            throw;
        }

        Task SaveAsync(string state) => File.WriteAllTextAsync(evidencePath,
            JsonSerializer.Serialize(new CaseEvidence(tool, marker, original, state, steps), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        async Task<ConversationResult> SendStepAsync(ActionSpec spec, bool approve, ConversationResult? previous)
        {
            var requests = 0; var matches = 0; var approvals = 0;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            var result = await provider.SendAsync(new(directory, spec.Prompt, previous?.Model, previous?.NativeSessionId,
                Access: ConversationAccess.WorkspaceWrite, PermissionHandler: (permission, _) =>
                {
                    var number = Interlocked.Increment(ref requests);
                    var match = Matches(permission, spec);
                    if (match) Interlocked.Increment(ref matches);
                    var accept = approve && number == 1 && match;
                    if (accept) Interlocked.Increment(ref approvals);
                    Console.WriteLine($"{tool}: ação {(accept ? "autorizada" : "recusada")}; prévia {(match ? "confere" : "divergente")}.");
                    return Task.FromResult(accept);
                }), new SilentProgress(), deadline.Token).ConfigureAwait(false);
            steps.Add(new(approve ? "Allow" : "Deny", requests, matches, approvals, result.Outcome.ToString(),
                result.NativeSessionId, result.Model, result.TokenUsage, Hashes(directory)));
            await SaveAsync(approve ? "AllowReturned" : "DenyReturned");
            if (requests != 1 || matches != 1 || approvals != (approve ? 1 : 0)
                || result.Outcome != (approve ? ConversationOutcome.Completed : ConversationOutcome.Blocked)
                || (approve ? result.PermissionDenials.Count != 0 : !result.PermissionDenials.Contains(tool))
                || previous is not null && (result.NativeSessionId != previous.NativeSessionId || result.Model != previous.Model))
                throw new ProviderException($"Prova {tool} não cumpriu os critérios; não iniciar outro turno.");
            return result;
        }
    }

    internal static bool Matches(ConversationPermission permission, ActionSpec spec)
    {
        try
        {
            if (permission.Provider != ProviderKind.Claude || permission.Kind != spec.Tool
                || !SamePath(permission.WorkingDirectory, spec.Directory)) return false;
            var separator = permission.Description.IndexOf("\n\n", StringComparison.Ordinal);
            if (separator < 0) return false;
            using var preview = JsonDocument.Parse(permission.Description[(separator + 2)..]);
            var input = preview.RootElement;
            if (input.ValueKind != JsonValueKind.Object) return false;
            var fields = input.EnumerateObject().Select(p => p.Name).ToArray();
            if (fields.Distinct(StringComparer.Ordinal).Count() != fields.Length) return false;
            if (spec.Tool == "Edit")
                return input.EnumerateObject().All(p => p.Name is "file_path" or "old_string" or "new_string" or "replace_all")
                    && SamePath(input.GetProperty("file_path").GetString()!, spec.FilePath, spec.Directory)
                    && input.GetProperty("old_string").GetString() == spec.Original
                    && input.GetProperty("new_string").GetString() == spec.Marker
                    && (!input.TryGetProperty("replace_all", out var replace) || replace.ValueKind == JsonValueKind.False);
            return input.EnumerateObject().All(p => p.Name is "command" or "description" or "timeout" or "run_in_background" or "dangerouslyDisableSandbox")
                && input.GetProperty("command").GetString() == spec.Command
                && (!input.TryGetProperty("run_in_background", out var background) || background.ValueKind == JsonValueKind.False)
                && (!input.TryGetProperty("dangerouslyDisableSandbox", out var unsafeMode) || unsafeMode.ValueKind == JsonValueKind.False)
                && (!input.TryGetProperty("timeout", out var timeout) || timeout.TryGetInt32(out var milliseconds) && milliseconds is > 0 and <= 10000);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or NotSupportedException)
        { return false; }
    }

    private static bool SamePath(string left, string right, string? basePath = null) =>
        Path.GetFullPath(left, basePath ?? Environment.CurrentDirectory).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> Hashes(string directory) => new Dictionary<string, string?>
    {
        ["negado.txt"] = HashFile(Path.Combine(directory, "negado.txt")), ["permitido.txt"] = HashFile(Path.Combine(directory, "permitido.txt"))
    };
    private static string? HashFile(string file) => File.Exists(file) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))) : null;

    private static void CheckFiles(string directory, string tool, string original, string marker, bool allowed)
    {
        var expected = tool == "Edit" ? new[] { "negado.txt", "permitido.txt" } : allowed ? new[] { "permitido.txt" } : [];
        if (!Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName).Order().SequenceEqual(expected.Order()))
            throw new ProviderException("A prova criou entradas inesperadas ou não produziu o arquivo previsto.");
        foreach (var file in expected)
        {
            var text = allowed && file == "permitido.txt" ? marker : original;
            if (!File.ReadAllBytes(Path.Combine(directory, file)).SequenceEqual(Encoding.UTF8.GetBytes(text)))
                throw new ProviderException("Conteúdo da prova divergiu; efeitos preservados para revisão.");
        }
    }

    public static void VerifyFiles(string root)
    {
        foreach (var tool in new[] { "Bash", "Edit" })
        {
            var evidence = JsonSerializer.Deserialize<CaseEvidence>(File.ReadAllText(Path.Combine(root, tool + "-evidence.json")))
                ?? throw new ProviderException("Evidência ausente.");
            if (evidence.State != "Completed" || evidence.Tool != tool || evidence.Steps.Count != 2
                || evidence.Steps[0].Step != "Deny" || evidence.Steps[1].Step != "Allow"
                || evidence.Steps[0].Outcome != "Blocked" || evidence.Steps[1].Outcome != "Completed"
                || evidence.Steps[0].Approvals != 0 || evidence.Steps[1].Approvals != 1
                || evidence.Steps.Any(s => s.Requests != 1 || s.MatchingRequests != 1)
                || evidence.Steps[0].SessionId != evidence.Steps[1].SessionId || evidence.Steps[0].Model != evidence.Steps[1].Model)
                throw new ProviderException("Evidência de permissões incompleta ou inconsistente.");
            var originalHash = tool == "Bash" ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence.Original)));
            if (new[] { "negado.txt", "permitido.txt" }.Any(file => !evidence.Steps[0].FileHashes.TryGetValue(file, out var hash) || hash != originalHash))
                throw new ProviderException("Evidência da negativa divergiu do estado inicial previsto.");
            var directory = Path.Combine(root, tool);
            CheckFiles(directory, tool, evidence.Original, evidence.Marker, allowed: true);
            var hashes = Hashes(directory);
            if (hashes.Any(p => !evidence.Steps[1].FileHashes.TryGetValue(p.Key, out var hash) || hash != p.Value))
                throw new ProviderException("Arquivos da prova mudaram após a conferência.");
        }
    }

    private sealed class SilentProgress : IProgress<ConversationEvent>
    {
        public void Report(ConversationEvent value) { }
    }
}
