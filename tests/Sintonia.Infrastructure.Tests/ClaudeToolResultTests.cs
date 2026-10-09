using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ClaudeToolResultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorrelatesStringAndTextBlocksWithoutForwardingImagesOrExtraFields(bool blocks)
    {
        var results = new List<ClaudeToolResult>();
        var parser = new ClaudeStreamParser("session", new QuietProgress(), results.Add);
        parser.Accept(Call());
        object content = blocks ? new object[] { new { type = "text", text = "conteúdo" },
            new { type = "image", data = "OMITIR" }, new { type = "text", text = "público" } } : "conteúdo público";
        parser.Accept(Result(content));
        var result = Assert.Single(results);
        Assert.Equal("call", result.ToolUseId); Assert.Equal("mcp__example__read", result.ToolName);
        Assert.False(result.IsError); Assert.False(result.Truncated);
        Assert.Equal(blocks ? "conteúdo\npúblico" : "conteúdo público", result.Text);
        Assert.DoesNotContain("OMITIR", result.Text);
    }

    [Fact]
    public void IgnoresUncorrelatedHistorySubagentAndDuplicateResults()
    {
        var results = new List<ClaudeToolResult>(); var parser = new ClaudeStreamParser("session", new QuietProgress(), results.Add);
        parser.Accept(Result("histórico"));
        parser.Accept(Call());
        parser.Accept(Result("subagente", parent: "parent"));
        Assert.Empty(results);
        parser.Accept(Result("principal")); parser.Accept(Result("repetido"));
        Assert.Equal("principal", Assert.Single(results).Text);
        Assert.Throws<ProviderException>(() => parser.Accept(Call()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundsObservationAndPreservesErrors(bool error)
    {
        var results = new List<ClaudeToolResult>(); var parser = new ClaudeStreamParser("session", new QuietProgress(), results.Add);
        parser.Accept(Call()); parser.Accept(Result(new string('á', 128_001), error));
        var result = Assert.Single(results); Assert.Equal(128_000, result.Text.Length);
        Assert.True(result.Truncated); Assert.Equal(error, result.IsError);
    }

    [Fact]
    public void DefaultParserDoesNotRequireNewToolIdsOrInspectResultBodies()
    {
        var parser = new ClaudeStreamParser("session", new QuietProgress());
        parser.Accept("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\"}]}}");
        parser.Accept("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"PRIVATE\"}]}}");
    }

    [Theory]
    [InlineData(513, 1)]
    [InlineData(1, 1025)]
    public void BoundsIdentifierStorageAndCallCount(int identifierLength, int calls)
    {
        var parser = new ClaudeStreamParser("session", new QuietProgress(), _ => { });
        Assert.Throws<ProviderException>(() =>
        {
            for (var index = 0; index < calls; index++)
                parser.Accept(JsonSerializer.Serialize(new { type = "assistant", message = new { content = new[]
                    { new { type = "tool_use", id = index + new string('a', identifierLength - 1), name = "Read" } } } }));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealAdapterCarriesOptInObservationThroughPermissionSessionUsingOnlyTestProcess(bool approve)
    {
        var results = new List<ClaudeToolResult>();
        var launch = new ExecutableLaunch(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), ["claude-host-mcp-result"], "Teste");
        var provider = new ClaudeConversationProvider(launch, results.Add);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await provider.SendAsync(new(AppContext.BaseDirectory, "SIMULAÇÃO",
            Access: ConversationAccess.WorkspaceWrite, PermissionHandler: (_, _) => Task.FromResult(approve)), new QuietProgress(), timeout.Token);
        Assert.Equal(approve ? ConversationOutcome.Completed : ConversationOutcome.Blocked, result.Outcome);
        Assert.Equal(!approve, Assert.Single(results).IsError);
        Assert.StartsWith("SIMULAÇÃO", results[0].Text);
    }

    private static string Call() => JsonSerializer.Serialize(new { type = "assistant", session_id = "session",
        message = new { content = new[] { new { type = "tool_use", id = "call", name = "mcp__example__read" } } } });
    private static string Result(object content, bool error = false, string? parent = null) => JsonSerializer.Serialize(new
    {
        type = "user", session_id = "session", parent_tool_use_id = parent,
        message = new { content = new[] { new { type = "tool_result", tool_use_id = "call", content, is_error = error } } }
    });
    private sealed class QuietProgress : IProgress<ConversationEvent> { public void Report(ConversationEvent value) { } }
}
