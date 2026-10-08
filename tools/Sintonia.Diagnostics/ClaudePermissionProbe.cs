using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudePermissionProbe
{
    public static async Task RunAsync(CancellationToken token)
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Permissões Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(directory);
        var launch = ExecutableLocator.Find(ProviderKind.Claude)
            ?? throw new ProviderException("Claude não encontrado.");
        // Add ask rules only to this test process; preserve user settings, hooks and extensions.
        var settings = JsonSerializer.Serialize(new { permissions = new { ask = new[] { "Write", "Edit", "Bash", "PowerShell" } } });
        var provider = new ClaudeConversationProvider(launch with
        {
            PrefixArguments = launch.PrefixArguments.Concat(new[] { "--settings", settings, "--max-turns", "3" }).ToArray()
        });
        var events = new InlineProgress<ConversationEvent>(ev =>
        {
            if (ev.Kind is ConversationEventKind.PermissionDenied or ConversationEventKind.Diagnostic)
                Console.WriteLine($"{ev.Kind}: {ev.Text}");
        });
        Console.WriteLine("Prova real Claude: dois turnos no máximo; recusa e autorização de Write em pasta exclusiva de teste.");
        var marker = "SINTONIA-PERMISSION-" + Guid.NewGuid().ToString("N");
        var deniedPath = Path.Combine(directory, "negado.txt");
        var allowedPath = Path.Combine(directory, "permitido.txt");
        var deniedCount = 0;
        var allowedCount = 0;
        bool Matches(ConversationPermission permission, string expectedPath)
        {
            if (permission.Provider != ProviderKind.Claude || permission.Kind != "Write") return false;
            var separator = permission.Description.IndexOf("\n\n", StringComparison.Ordinal);
            if (separator < 0) return false;
            using var preview = JsonDocument.Parse(permission.Description[(separator + 2)..]);
            var input = preview.RootElement;
            return input.TryGetProperty("file_path", out var file) && input.TryGetProperty("content", out var content)
                && Path.GetFullPath(file.GetString()!, directory).Equals(expectedPath, StringComparison.OrdinalIgnoreCase)
                && content.GetString()?.TrimEnd('\r', '\n') == marker;
        }
        string Prompt(string fileName) => $"Teste finito de autorização. Use somente a ferramenta Write, uma única vez, para criar {fileName} nesta pasta com o conteúdo exato {marker}. "
            + "Não leia arquivos, não use shell, MCPs, outros agentes nem outras ferramentas. Se houver recusa, pare sem repetir ou contornar. Depois responda em uma frase curta.";
        var denied = await provider.SendAsync(new(directory, Prompt("negado.txt"), Access: ConversationAccess.WorkspaceWrite,
            PermissionHandler: (permission, _) =>
            {
                if (Matches(permission, deniedPath)) deniedCount++;
                return Task.FromResult(false);
            }), events, token);
        if (deniedCount != 1 || denied.Outcome != ConversationOutcome.Blocked || File.Exists(deniedPath))
            throw new ProviderException("A prova de recusa não cumpriu os critérios; não iniciar outro turno.");
        var allowed = await provider.SendAsync(new(directory, Prompt("permitido.txt"), denied.Model, denied.NativeSessionId,
            Access: ConversationAccess.WorkspaceWrite, PermissionHandler: (permission, _) =>
            {
                var approve = allowedCount == 0 && Matches(permission, allowedPath);
                if (approve) allowedCount++;
                return Task.FromResult(approve);
            }), events, token);
        if (allowedCount != 1 || allowed.Outcome != ConversationOutcome.Completed || allowed.NativeSessionId != denied.NativeSessionId
            || !File.Exists(allowedPath) || (await File.ReadAllTextAsync(allowedPath, token)).TrimEnd('\r', '\n') != marker
            || File.Exists(deniedPath)) throw new ProviderException("A prova de autorização/retomada não cumpriu os critérios.");
        await File.WriteAllTextAsync(Path.Combine(directory, "evidence.json"), JsonSerializer.Serialize(new
        {
            testedAt = DateTimeOffset.Now, denied.NativeSessionId, allowed.Model,
            deniedCount, allowedCount, deniedOutcome = denied.Outcome.ToString(), allowedOutcome = allowed.Outcome.ToString(),
            deniedFileAbsent = true, allowedFileMatches = true
        }, new JsonSerializerOptions { WriteIndented = true }), token);
        Console.WriteLine($"PASS: recusa sem arquivo, autorização de uma ação com conteúdo correto e retomada. Modelo: {allowed.Model}. Evidência local: {directory}");
    }
}
