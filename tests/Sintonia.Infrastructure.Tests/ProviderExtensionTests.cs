using System.Text.Json;
using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;
using Sintonia.Infrastructure.Providers;

namespace Sintonia.Infrastructure.Tests;

public sealed class ProviderExtensionTests
{
    private static ExecutableLaunch Fixture(string scenario) => new(Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe"), [scenario], "Teste");
    private static IProviderExtensionReader Reader(string scenario) => scenario.StartsWith("rpc", StringComparison.Ordinal)
        ? new CodexConversationProvider(Fixture(scenario)) : new ClaudeConversationProvider(Fixture(scenario));

    [Fact]
    public async Task CodexProjectsDisabledSkillsOwnershipAndEveryMcpPageWithoutStartingThread()
    {
        var snapshot = await Reader("rpc-extensions").ReadExtensionsAsync(AppContext.BaseDirectory, CancellationToken.None);
        Assert.Equal(4, snapshot.Extensions.Count);
        Assert.Contains(snapshot.Extensions, e => e.Name == "disabled-skill" && e.Enabled == false);
        Assert.Contains(snapshot.Extensions, e => e.Name == "active-skill" && e.PluginId == "example-plugin");
        Assert.Contains(snapshot.Extensions, e => e.Name == "first-server" && e.Status == null && e.AuthStatus == "unsupported" && e.ToolCount == 1);
        Assert.Contains(snapshot.Extensions, e => e.Name == "second-server" && e.Status == "authenticationRequired" && e.AuthStatus == "notLoggedIn" && e.DiscoveryFailed);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
        Assert.Contains(snapshot.Warnings, w => w.Contains("plugin/list"));
    }

    [Fact]
    public async Task CodexCapabilitiesUseSameCompleteInventoryAndHideDisabledSkills()
    {
        var snapshot = await new CodexConversationProvider(Fixture("rpc-extensions")).InspectAsync(AppContext.BaseDirectory, CancellationToken.None);
        Assert.Contains(snapshot.Extensions, e => e.Contains("second-server") && e.Contains("autenticação necessária"));
        Assert.DoesNotContain(snapshot.Extensions, e => e.Contains("disabled-skill"));
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData("claude-extensions")]
    [InlineData("claude-extensions-control")]
    public async Task ClaudeSeparatesInstalledPluginsCommandsAndMcpStateWithoutPrompt(string scenario)
    {
        var snapshot = await Reader(scenario).ReadExtensionsAsync(AppContext.BaseDirectory, CancellationToken.None);
        Assert.Equal(4, snapshot.Extensions.Count);
        Assert.Contains(snapshot.Extensions, e => e.Kind == ProviderExtensionKind.Plugin && e.Enabled == false);
        Assert.Contains(snapshot.Extensions, e => e.Kind == ProviderExtensionKind.Command && e.Name == "example:review");
        Assert.DoesNotContain(snapshot.Extensions, e => e.Kind == ProviderExtensionKind.Skill);
        Assert.Contains(snapshot.Extensions, e => e.Status == "needs-auth" && e.ToolCount == 1 && e.DiscoveryFailed);
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
        if (scenario.EndsWith("control", StringComparison.Ordinal)) Assert.Contains(snapshot.Warnings, w => w.Contains("recusados durante"));
    }

    [Theory]
    [InlineData("rpc-extensions-repeat")]
    [InlineData("rpc-extensions-many")]
    [InlineData("rpc-extensions-error")]
    [InlineData("rpc-extensions-invalid")]
    [InlineData("rpc-extensions-exit")]
    [InlineData("claude-extensions-error")]
    [InlineData("claude-extensions-invalid")]
    [InlineData("claude-extensions-exit")]
    public async Task IncompleteMetadataFailsWithoutLeakingInternalDetails(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<ProviderException>(() => Reader(scenario).ReadExtensionsAsync(AppContext.BaseDirectory, timeout.Token));
        Assert.DoesNotContain("SEGREDO", error.Message);
    }

    [Theory]
    [InlineData("rpc-extensions-wait")]
    [InlineData("claude-extensions-wait")]
    public async Task CancellationInterruptsMetadataProcess(string scenario)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(scenario).ReadExtensionsAsync(AppContext.BaseDirectory, timeout.Token));
    }

    [Fact]
    public async Task FutureStateValuesRemainUnknownWithoutExposingUnrecognizedText()
    {
        var snapshot = await Reader("rpc-extensions-future").ReadExtensionsAsync(AppContext.BaseDirectory, CancellationToken.None);
        Assert.All(snapshot.Extensions.Where(e => e.Kind == ProviderExtensionKind.Mcp), e => { Assert.Null(e.Status); Assert.Null(e.AuthStatus); });
        Assert.DoesNotContain("SEGREDO", JsonSerializer.Serialize(snapshot));
    }
}
