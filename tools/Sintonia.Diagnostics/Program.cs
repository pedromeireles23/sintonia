using System.Text;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

Console.OutputEncoding = Encoding.UTF8;
using var cancellation = new CancellationTokenSource(args.Length == 0 ? TimeSpan.FromSeconds(25)
    : args.FirstOrDefault() is "collaboration" or "collaboration-resume" ? TimeSpan.FromMinutes(6) : TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    if (args is ["extensions", var extensionName] && Enum.TryParse<ProviderKind>(extensionName, true, out var extensionProvider) && Enum.IsDefined(extensionProvider))
    {
        IProviderExtensionReader reader = extensionProvider == ProviderKind.Codex ? new CodexConversationProvider() : new ClaudeConversationProvider();
        try
        {
            var snapshot = await reader.ReadExtensionsAsync(Environment.CurrentDirectory, cancellation.Token);
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            }));
            Console.WriteLine("Inventário de metadados encerrado; nenhum prompt ou turno de modelo enviado. Nomes/estados projetados, sem configurações, credenciais ou stderr.");
            return 0;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.Error.WriteLine("Inventário de extensões não concluído; detalhes internos omitidos. Nenhum prompt foi enviado."); return 1;
        }
    }
    if (args is ["verify-collaboration", var stage, var marker])
    {
        if (stage is not ("data" or "complete")) throw new ProviderException("Etapa de verificação inválida.");
        WorkspaceCollaborationProbe.VerifyFiles(Environment.CurrentDirectory, marker, stage == "complete");
        Console.WriteLine("PASS: arquivos da colaboração conferidos sem modelos."); return 0;
    }
    if (args is ["collaboration"])
    {
        await WorkspaceCollaborationProbe.RunAsync(cancellation.Token); return 0;
    }
    if (args is ["collaboration-resume", var probeDirectory])
    {
        await WorkspaceCollaborationProbe.ResumeAsync(probeDirectory, cancellation.Token); return 0;
    }
    if (args is ["usage", var usageName] && Enum.TryParse<ProviderKind>(usageName, true, out var usageProvider) && Enum.IsDefined(usageProvider))
    {
        IProviderUsageReader reader = usageProvider == ProviderKind.Codex ? new CodexConversationProvider() : new ClaudeConversationProvider();
        var snapshot = await reader.ReadUsageAsync(Environment.CurrentDirectory, cancellation.Token);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(snapshot, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("Consulta de metadados encerrada; nenhuma thread/turno/inferência iniciado e nenhuma credencial exibida.");
        return 0;
    }
    if (args is ["queue"])
    {
        await WorkspaceQueueProbe.RunAsync(cancellation.Token); return 0;
    }
    if (args is ["plan", var plannerName] && Enum.TryParse<ProviderKind>(plannerName, true, out var planner))
    {
        var directory = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Plano " + planner + " " + Guid.NewGuid()));
        Directory.CreateDirectory(directory);
        IConversationProvider adapter = planner == ProviderKind.Codex ? new CodexConversationProvider() : new ClaudeConversationProvider();
        var result = await adapter.SendAsync(new(directory,
            "Proponha exatamente duas tarefas para um formulário de portal: implementação por Codex e revisão por Claude dependente da primeira. "
            + "Use escopo src/ e critérios verificáveis. Modelo null em ambas. Sem ferramentas, arquivos ou agentes adicionais; apenas produza a proposta para revisão.",
            Instructions: PlanProposalFormat.ChiefInstructions), new InlineProgress<ConversationEvent>(_ => { }), cancellation.Token);
        if (result.Outcome != ConversationOutcome.Completed || !PlanProposalFormat.TryParseResponse(result.Text, out var plan, out _)
            || plan!.Tasks.Count != 2 || plan.Tasks.Select(t => t.Provider).Distinct().Count() != 2
            || plan.Tasks.Count(t => t.Dependencies.Count > 0) != 1)
            throw new ProviderException("A prova de proposta estruturada não cumpriu os critérios.");
        await File.WriteAllTextAsync(Path.Combine(directory, "plan.json"), PlanProposalFormat.Serialize(plan), cancellation.Token);
        Console.WriteLine($"PASS: um turno real de planejamento {planner}, modelo {result.Model}; duas tarefas, ambos os provedores e dependência validados. Plano local: {directory}");
        return 0;
    }
    if (args is ["permissions", "Claude"])
    {
        await ClaudePermissionProbe.RunAsync(cancellation.Token);
        return 0;
    }
    if (args is ["sandbox", var mode] && mode is "readOnly" or "workspaceWrite")
    {
        var launch = ExecutableLocator.Find(ProviderKind.Codex)!;
        await using var rpc = new JsonRpcClient(launch with { PrefixArguments = launch.PrefixArguments.Concat(new[] { "-c", "windows.sandbox=\"unelevated\"" }).ToArray() }, Environment.CurrentDirectory);
        await rpc.RequestAsync("initialize", new { clientInfo = new { name = "sintonia", version = "0.1.0" } }, cancellation.Token);
        await rpc.NotifyAsync("initialized", new { }, cancellation.Token);
        var readiness = await rpc.RequestAsync("windowsSandbox/readiness", null, cancellation.Token);
        Console.WriteLine("Windows sandbox: " + readiness.GetProperty("status").GetString());
        var result = await rpc.RequestAsync("command/exec", new { command = new[] { "rtk", "proxy", "powershell", "-NoProfile", "-Command", "Get-Content -LiteralPath 'amostra.txt' -Encoding UTF8" },
            cwd = Path.GetFullPath("artifacts/provider-probes/Projeto ação com espaços"), sandboxPolicy = new { type = mode }, timeoutMs = 10000 }, cancellation.Token);
        Console.WriteLine(result.ToString());
        if (mode == "readOnly")
        {
            var folder = Path.GetFullPath("artifacts/provider-probes/Projeto ação com espaços");
            var denial = await rpc.RequestAsync("command/exec", new { command = new[] { "powershell", "-NoProfile", "-Command", "Set-Content -LiteralPath 'escrita-negada.txt' -Value 'não deve existir' -ErrorAction Stop" },
                cwd = folder, sandboxPolicy = new { type = "readOnly" }, timeoutMs = 10000 }, cancellation.Token);
            if (denial.GetProperty("exitCode").GetInt32() == 0 || File.Exists(Path.Combine(folder, "escrita-negada.txt")))
                throw new ProviderException("A escrita não foi bloqueada pelo isolamento de leitura.");
            Console.WriteLine("Negativa de escrita pelo isolamento confirmada; arquivo não criado.");
        }
        return 0;
    }
    if (args is ["interrupt", var interruptName] && Enum.TryParse<ProviderKind>(interruptName, true, out var interruptProvider))
    {
        IConversationProvider adapter = interruptProvider == ProviderKind.Codex ? new CodexConversationProvider() : new ClaudeConversationProvider();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        var streamed = false;
        var progress = new InlineProgress<ConversationEvent>(ev =>
        {
            if (ev.Kind == ConversationEventKind.TextDelta && !streamed) { streamed = true; stop.CancelAfter(50); }
        });
        try
        {
            await adapter.SendAsync(new(Path.GetFullPath("artifacts/provider-probes/Projeto ação com espaços"),
                "Sem ferramentas, arquivos ou outros agentes, escreva vinte parágrafos curtos sobre boas práticas de nomes de variáveis. Comece imediatamente."), progress, stop.Token);
            throw new ProviderException("O turno concluiu antes da interrupção; esta prova não confirma cancelamento.");
        }
        catch (OperationCanceledException) when (streamed && stop.IsCancellationRequested)
        {
            Console.WriteLine($"{interruptProvider}: stream real recebido e execução interrompida; processo/filhos encerrados pelo adaptador.");
            return 0;
        }
    }
    if (args is ["handshake"])
    {
        var capabilities = await new CodexConversationProvider().InspectAsync(Environment.CurrentDirectory, cancellation.Token);
        Console.WriteLine("Handshake Codex concluído; login ChatGPT confirmado; nenhuma inferência.");
        Console.WriteLine("Modelos: " + string.Join(", ", capabilities.Models.Select(m => m.Id)));
        Console.WriteLine("Extensões: " + string.Join(", ", capabilities.Extensions));
        foreach (var warning in capabilities.Warnings) Console.WriteLine(warning);
        return 0;
    }
    if (args is ["real", var providerName] && Enum.TryParse<ProviderKind>(providerName, true, out var provider))
    {
        var directory = Path.GetFullPath("artifacts/provider-probes/Projeto ação com espaços");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "amostra.txt"), "Pedidos: 12, 8, 5. Soma esperada: 25.\nMarcador de retomada: SINTONIA-7341.", cancellation.Token);
        IConversationProvider adapter = provider == ProviderKind.Codex ? new CodexConversationProvider() : new ClaudeConversationProvider();
        var progress = new InlineProgress<ConversationEvent>(ev =>
        {
            if (ev.Kind != ConversationEventKind.TextDelta) Console.WriteLine($"{ev.Kind}: {ev.Text}");
        });
        Console.WriteLine($"Prova real limitada: {provider}; dois turnos; login por assinatura; leitura, sem bypass.");
        var first = await adapter.SendAsync(new(directory,
            "Leia amostra.txt com a ferramenta de leitura disponível e verifique a soma dos pedidos. Responda em português com a soma e o marcador. Não altere arquivos e não use agentes adicionais."), progress, cancellation.Token);
        Console.WriteLine($"Primeiro turno: {first.Outcome}; modelo {first.Model}; {first.Text}");
        if (first.Outcome != ConversationOutcome.Completed || !first.Text.Contains("25") || !first.Text.Contains("SINTONIA-7341"))
            throw new ProviderException("A prova de leitura não atendeu aos critérios.");
        var second = await adapter.SendAsync(new(directory,
            "Sem ler arquivos nem usar ferramentas, diga qual era a soma e o marcador verificados na mensagem anterior. Responda em uma frase.",
            first.Model, first.NativeSessionId), progress, cancellation.Token);
        Console.WriteLine($"Retomada: {second.Outcome}; {second.Text}");
        if (second.NativeSessionId != first.NativeSessionId || second.Outcome != ConversationOutcome.Completed
            || !second.Text.Contains("25") || !second.Text.Contains("SINTONIA-7341")) throw new ProviderException("Retomada não comprovada.");
        await File.WriteAllTextAsync(Path.Combine(directory, $"{provider}-evidence.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            provider = provider.ToString(), first.NativeSessionId, first.Model, first.Outcome,
            firstResult = first.Text, resumedResult = second.Text, testedAt = DateTimeOffset.Now
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }), cancellation.Token);
        Console.WriteLine("Leitura real e retomada verificadas. Evidência local em artifacts/provider-probes.");
        return 0;
    }
    if (args.Length != 0) { Console.Error.WriteLine("Uso: sem argumentos | extensions Codex/Claude | usage Codex/Claude | handshake | sandbox readOnly | real Codex/Claude | interrupt Codex/Claude | permissions Claude | plan Codex/Claude | queue | collaboration | collaboration-resume pasta | verify-collaboration data/complete marcador"); return 1; }
    Console.WriteLine("Sintonia · diagnóstico limitado de instalações\nNenhuma inferência será iniciada. Não confirma login, quota ou integração real.\n");
    var probe = new ProviderInstallationProbe();
    var reports = await Task.WhenAll(Enum.GetValues<ProviderKind>().Select(provider =>
        probe.InspectAsync(provider, Environment.CurrentDirectory, cancellation.Token)));
    foreach (var report in reports)
    {
        var status = report.Status switch { InstallationStatus.Detected => "Detectado", InstallationStatus.Missing => "Ausente", InstallationStatus.Unsupported => "Instalação não suportada", _ => "Falha no diagnóstico" };
        Console.WriteLine($"{report.Provider}: {status} · {report.Version ?? "versão indisponível"}");
        if (report.InstallationKind is { } kind) Console.WriteLine($"  Instalação: {kind}");
        if (report.AdvertisedOptions.Count > 0) Console.WriteLine($"  Opções anunciadas na ajuda: {string.Join(", ", report.AdvertisedOptions)}");
        Console.WriteLine($"  {report.Diagnostic}\n");
    }
    return reports.All(r => r.Status == InstallationStatus.Detected) ? 0 : 1;
}
catch (OperationCanceledException) { Console.Error.WriteLine("Diagnóstico cancelado ou prazo excedido."); return 2; }
catch (ProviderException exception) { Console.Error.WriteLine(exception.Message); return 1; }
catch (Exception exception) when (args.FirstOrDefault() is "collaboration" or "collaboration-resume")
{ Console.Error.WriteLine("Prova interrompida; entregas preservadas e nenhum reenvio automático. " + exception.Message); return 1; }

sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
