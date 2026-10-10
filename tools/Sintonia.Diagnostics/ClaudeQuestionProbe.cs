using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

internal static class ClaudeQuestionProbe
{
    internal const string Tool = "AskUserQuestion";
    internal const string Question = "Qual formato usar nesta prova do Sintonia?";
    internal const string Answer = "Detalhes";
    internal static ConversationQuestion Expected => new(Question, "Formato", false,
        [new("Resumo", "Apresenta apenas os resultados principais."), new(Answer, "Inclui explicações e exemplos.")]);
    internal static string Input => JsonSerializer.Serialize(new { questions = new[] { new
    {
        question = Question, header = Expected.Header, multiSelect = false,
        options = Expected.Choices.Select(choice => new { label = choice.Label, description = choice.Description })
    } } });
    internal static string Prompt => "Prova finita de pergunta do Sintonia. Use exatamente uma vez a ferramenta AskUserQuestion com esta entrada exata:\n"
        + Input + "\nAguarde a resposta do host; não escolha pelo usuário. Depois responda somente um objeto JSON com a chave formato e o rótulo recebido como valor, sem bloco Markdown. "
        + "Não use outras ferramentas, arquivos ou agentes. Se houver recusa ou erro, pare sem repetir nem contornar. Pare após confirmar a resposta.";
    internal sealed record ResultEvidence(string ToolUseId, string ToolName, bool? IsError, int TextLength,
        string TextHash, bool Truncated, bool QuestionAndAnswerFound);
    internal sealed record Evidence(string Version, string State, int Attempts, int Requests, int MatchingRequests,
        int Answers, int Permissions, int ToolCalls, string? Outcome, string? SessionId, string? Model,
        bool FinalAnswerMatches, RunTokenUsage? TokenUsage, IReadOnlyList<ResultEvidence> Results, string? FailureCode = null);

    public static async Task RunAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Pergunta Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        Console.WriteLine("Prova real AskUserQuestion: uma pergunta/um envio no máximo, 90s/três iterações, sem retry.");
        Console.WriteLine("Pasta exclusiva e evidência parcial: " + root);
        var native = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var launch = CreateLaunch(native);
        try
        {
            var version = await ProcessProbe.RunAsync(native, ["--version"], root, TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
            if (version.ExitCode != 0 || version.TimedOut || version.Truncated || version.StandardOutput.Trim().Length is < 1 or > 100)
                throw new ProviderException("Não foi possível conferir a versão Claude.");
            await new ClaudeConversationProvider(launch).CheckSubscriptionAsync(root, token).ConfigureAwait(false);
            await CheckReadyAsync(launch, root, token).ConfigureAwait(false);
            await RunCaseAsync(root, observe => new ClaudeConversationProvider(launch, observe), version.StandardOutput.Trim(), token).ConfigureAwait(false);
            Verify(root);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            if (IsOAuthRefreshFailure(exception))
                throw new ProviderException("Claude falhou ao renovar o login OAuth; refaça o login por assinatura antes de uma nova prova. Evidência preservada, sem reenvio.");
            throw new ProviderException("Prova de pergunta não concluída; pasta e eventual evidência parcial preservadas, sem reenvio. Detalhes internos omitidos.");
        }
        Console.WriteLine("PASS: uma pergunta real exata, resposta Detalhes e resultado de ferramenta correlacionados; leitura sem autorização de escrita.");
    }

    internal static ExecutableLaunch CreateLaunch(ExecutableLaunch native) => native with
    {
        PrefixArguments = native.PrefixArguments.Concat(new[]
        {
            "--settings", JsonSerializer.Serialize(new { permissions = new { ask = new[] { Tool } } }),
            "--mcp-config", "{\"mcpServers\":{}}", "--strict-mcp-config", "--tools", Tool,
            "--max-turns", "3" // Scalar last so variadic tool flags do not consume auth/status.
        }).ToArray()
    };

    public static async Task CheckReadyAsync(CancellationToken token)
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "provider-probes", "Inicialização pergunta Claude " + Guid.NewGuid()));
        Directory.CreateDirectory(root);
        var native = ExecutableLocator.Find(ProviderKind.Claude) ?? throw new ProviderException("Claude não encontrado.");
        var launch = CreateLaunch(native);
        await new ClaudeConversationProvider(launch).CheckSubscriptionAsync(root, token).ConfigureAwait(false);
        await CheckReadyAsync(launch, root, token).ConfigureAwait(false);
        Console.WriteLine("PASS: assinatura e initialize da prova em leitura conferidos; nenhum prompt, modelo ou resposta enviados.");
    }

    internal static async Task CheckReadyAsync(ExecutableLaunch launch, string root, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(25)); token = deadline.Token;
        var initialized = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var process = new ProviderProcess(launch,
            ["--print", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
                "--permission-prompt-tool", "stdio", "--permission-mode", "plan", "--permission-prompts", "host", "--no-session-persistence"],
            root, line =>
            {
                using var doc = JsonDocument.Parse(line); var message = doc.RootElement;
                if (message.GetProperty("type").GetString() == "control_response")
                {
                    var response = message.GetProperty("response");
                    if (response.GetProperty("request_id").GetString() == "question-ready")
                        initialized.TrySetResult(response.GetProperty("subtype").GetString() == "success");
                }
                else if (message.GetProperty("type").GetString() == "control_request")
                    initialized.TrySetResult(false); // Metadata only; never accept an interactive request.
                return ValueTask.CompletedTask;
            });
        await process.WriteLineAsync(JsonSerializer.Serialize(new
            { type = "control_request", request_id = "question-ready", request = new { subtype = "initialize" } }), token).ConfigureAwait(false);
        var winner = await Task.WhenAny(initialized.Task, process.Completion).WaitAsync(token).ConfigureAwait(false);
        if (winner == initialized.Task && await initialized.Task.ConfigureAwait(false)) return;
        if (winner == process.Completion) await process.Completion.ConfigureAwait(false);
        var reason = process.StandardError.Contains("AskUserQuestion", StringComparison.Ordinal) ? "CLI recusou a configuração da ferramenta de pergunta"
            : process.StandardError.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ? "CLI recusou argumentos"
            : "CLI não confirmou initialize";
        throw new ProviderException(reason + "; nenhum prompt enviado. Mensagens internas omitidas.");
    }

    internal static async Task RunCaseAsync(string root, Func<Action<ClaudeToolResult>, IConversationProvider> createProvider,
        string version, CancellationToken token)
    {
        var path = Path.Combine(root, "question-evidence.json");
        if (Directory.EnumerateFileSystemEntries(root).Any()) throw new ProviderException("A pasta da prova precisa estar vazia; não reenviar turnos.");
        var requests = 0; var matches = 0; var answers = 0; var permissions = 0; var calls = 0; var attempts = 0;
        ConversationResult? returned = null;
        string? failure = null; string? observedSession = null; string? observedModel = null; RunTokenUsage? observedUsage = null;
        var results = new ConcurrentQueue<ResultEvidence>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(90));
        await SaveAsync("Prepared");
        try
        {
            var provider = createProvider(result => results.Enqueue(Project(result)));
            attempts = 1; await SaveAsync("Sending");
            returned = await provider.SendAsync(new(root, Prompt, Access: ConversationAccess.ReadOnly,
                PermissionHandler: (_, _) => { Interlocked.Increment(ref permissions); deadline.Cancel(); return Task.FromResult(false); },
                QuestionHandler: (question, questionToken) =>
                {
                    questionToken.ThrowIfCancellationRequested();
                    var number = Interlocked.Increment(ref requests);
                    var match = Matches(question, root);
                    if (match) Interlocked.Increment(ref matches);
                    if (number != 1 || !match) { deadline.Cancel(); return Task.FromResult<ConversationQuestionResponse?>(null); }
                    Interlocked.Increment(ref answers);
                    Console.WriteLine("Pergunta/formato/opções/pasta conferem; callback responde somente Detalhes.");
                    return Task.FromResult<ConversationQuestionResponse?>(new([new([Answer])]));
                }), new ImmediateProgress(Report), deadline.Token).ConfigureAwait(false);
            await SaveAsync("Returned");
            Check(Snapshot("Completed"));
            if (returned.PermissionDenials.Count != 0 || deadline.IsCancellationRequested
                || Directory.EnumerateFileSystemEntries(root).Any(entry => !string.Equals(entry, path, StringComparison.OrdinalIgnoreCase)))
                throw new ProviderException("A prova recebeu negativa/cancelamento ou criou entradas inesperadas.");
            await SaveAsync("Completed");
        }
        catch (Exception exception)
        {
            failure = IsOAuthRefreshFailure(exception) ? "OAuthRefreshFailed"
                : exception is OperationCanceledException || deadline.IsCancellationRequested ? "Cancelled" : "ProofFailed";
            await SaveAsync("Interrupted"); throw;
        }

        Evidence Snapshot(string state) => new(version, state, attempts, requests, matches, answers, permissions, calls,
            returned?.Outcome.ToString(), returned?.NativeSessionId ?? observedSession, returned?.Model ?? observedModel,
            returned is not null && FinalMatches(returned.Text), returned?.TokenUsage ?? observedUsage, results.ToArray(), failure);
        Task SaveAsync(string state) => File.WriteAllTextAsync(path,
            JsonSerializer.Serialize(Snapshot(state), new JsonSerializerOptions { WriteIndented = true }), CancellationToken.None);

        void Report(ConversationEvent ev)
        {
            if (ev.Kind == ConversationEventKind.Session) { observedSession = ev.NativeSessionId; observedModel = ev.Model; }
            if (ev.Kind == ConversationEventKind.TokenUsage) observedUsage = ev.TokenUsage;
            if (ev.Kind != ConversationEventKind.Tool) return;
            if (Interlocked.Increment(ref calls) != 1 || ev.Text != Tool) deadline.Cancel();
        }
    }

    internal static bool Matches(ConversationQuestionRequest request, string root)
    {
        if (request.Provider != ProviderKind.Claude || !ConversationQuestionPolicy.IsValid(request) || request.Questions.Count != 1
            || !string.Equals(request.WorkingDirectory, root, StringComparison.OrdinalIgnoreCase)) return false;
        var question = request.Questions[0]; var expected = Expected;
        return question.Text == expected.Text && question.Header == expected.Header && !question.MultiSelect
            && question.Choices.SequenceEqual(expected.Choices);
    }
    internal static bool FinalMatches(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.EnumerateObject().Count() == 1
                && doc.RootElement.TryGetProperty("formato", out var format) && format.ValueKind == JsonValueKind.String && format.GetString() == Answer;
        }
        catch (JsonException) { return false; }
    }
    internal static ResultEvidence Project(ClaudeToolResult result) => new(result.ToolUseId, result.ToolName, result.IsError,
        result.Text.Length, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result.Text))), result.Truncated,
        result.Text.Contains(Question, StringComparison.Ordinal) && result.Text.Contains(Answer, StringComparison.Ordinal));

    public static void Verify(string root)
    {
        var evidence = JsonSerializer.Deserialize<Evidence>(File.ReadAllText(Path.Combine(root, "question-evidence.json")))
            ?? throw new ProviderException("Evidência de pergunta ausente.");
        Check(evidence);
    }
    private static void Check(Evidence evidence)
    {
        if (evidence.State != "Completed" || evidence.FailureCode is not null || string.IsNullOrWhiteSpace(evidence.Version) || evidence.Attempts != 1
            || evidence.Requests != 1 || evidence.MatchingRequests != 1 || evidence.Answers != 1 || evidence.Permissions != 0
            || evidence.ToolCalls != 1 || evidence.Outcome != "Completed" || !evidence.FinalAnswerMatches
            || string.IsNullOrWhiteSpace(evidence.SessionId) || string.IsNullOrWhiteSpace(evidence.Model)
            || evidence.Results is null || evidence.Results.Count != 1
            || evidence.Results.Any(result => result.ToolName != Tool || string.IsNullOrWhiteSpace(result.ToolUseId)
                || result.IsError == true || result.Truncated || !result.QuestionAndAnswerFound || result.TextLength <= 0
                || result.TextHash.Length != 64 || !result.TextHash.All(Uri.IsHexDigit)))
            throw new ProviderException("A prova não confirmou uma única pergunta e a resposta esperada; não repetir chamadas.");
    }
    private static bool IsOAuthRefreshFailure(Exception exception) => exception is ProviderException { FailureKind: ProviderFailureKind.AuthenticationRefreshFailed };
    private sealed class ImmediateProgress(Action<ConversationEvent> report) : IProgress<ConversationEvent>
    {
        public void Report(ConversationEvent ev) => report(ev);
    }
}
