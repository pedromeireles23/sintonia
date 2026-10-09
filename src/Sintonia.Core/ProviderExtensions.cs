namespace Sintonia.Core;

public enum ProviderExtensionKind { Skill, Command, Plugin, Mcp }

/// <summary>Projected public metadata, never a server configuration or a proof of tool execution.</summary>
public sealed record ProviderExtension(ProviderExtensionKind Kind, string Name, bool? Enabled = null,
    string? Scope = null, string? Status = null, string? AuthStatus = null, int? ToolCount = null,
    string? PluginId = null, bool DiscoveryFailed = false);

public sealed record ProviderExtensionSnapshot(ProviderKind Provider, IReadOnlyList<ProviderExtension> Extensions,
    IReadOnlyList<string> Warnings);

public interface IProviderExtensionReader
{
    Task<ProviderExtensionSnapshot> ReadExtensionsAsync(string directory, CancellationToken token);
}
