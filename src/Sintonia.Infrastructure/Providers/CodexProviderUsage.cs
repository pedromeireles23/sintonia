using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

public sealed partial class CodexConversationProvider : IProviderUsageReader
{
    public async Task<ProviderUsageSnapshot> ReadUsageAsync(string directory, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await using var rpc = new JsonRpcClient(Launch, directory);
            await InitializeAsync(rpc, deadline.Token).ConfigureAwait(false);
            return await ReadUsageAsync(rpc, deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ProviderException("A consulta de uso do Codex excedeu vinte segundos e foi encerrada."); }
        catch (Exception error) when (error is ProviderException or FormatException or InvalidOperationException)
        { throw new ProviderException("Não foi possível consultar o uso do Codex. Confira a instalação, login ChatGPT e conexão; nenhum turno foi iniciado."); }
    }
    private static async Task<ProviderUsageSnapshot> ReadUsageAsync(JsonRpcClient rpc, CancellationToken token) =>
        CodexUsageParser.Parse(await rpc.RequestAsync("account/rateLimits/read", null, token).ConfigureAwait(false), DateTimeOffset.UtcNow);
    private static async Task CheckIncludedUsageAsync(JsonRpcClient rpc, IProgress<ConversationEvent> progress, CancellationToken token)
    {
        bool? allowed = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try { allowed = CodexUsageParser.IncludedUsageAllowed(await rpc.RequestAsync("account/rateLimits/read", null, deadline.Token).ConfigureAwait(false)); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception error) when (error is ProviderException or FormatException or InvalidOperationException) { }
        token.ThrowIfCancellationRequested();
        if (allowed == false)
            throw new ProviderException("O Codex informou que o uso incluído está indisponível. Nenhum turno foi iniciado; consulte os limites da conta antes de tentar novamente.");
        if (allowed is null)
            progress.Report(new(ConversationEventKind.Diagnostic, "Disponibilidade do uso incluído não confirmada. O Codex aplicará seus limites ao pedido; percentuais não autorizam consumo."));
    }
}
