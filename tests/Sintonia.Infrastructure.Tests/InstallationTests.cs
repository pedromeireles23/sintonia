using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Tests;

public sealed class InstallationTests
{
    [Theory]
    [InlineData(ProviderKind.Codex)]
    [InlineData(ProviderKind.Claude)]
    public async Task DetectionUsesOnlyVersionAndHelpOfAControlledLocalExecutable(ProviderKind provider)
    {
        using var folder = new TestFolder();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "fixtures")))
            File.Copy(file, Path.Combine(folder.Path, System.IO.Path.GetFileName(file)));
        var name = provider == ProviderKind.Codex ? "codex.exe" : "claude.exe";
        File.Copy(Path.Combine(folder.Path, "Sintonia.ProcessFixture.exe"), Path.Combine(folder.Path, name));
        var report = await new ProviderInstallationProbe(folder.Path).InspectAsync(provider, folder.Path);
        Assert.Equal(InstallationStatus.Detected, report.Status);
        Assert.Equal(provider == ProviderKind.Codex ? "0.1.0-test.1" : "2.1.0", report.Version);
        Assert.Contains(provider == ProviderKind.Codex ? "--stdio" : "--permission-mode", report.AdvertisedOptions);
        Assert.Contains("inferência ainda não", report.Diagnostic);
    }

    [Fact]
    public void KnownNpmNativeLayoutAvoidsTheShell()
    {
        using var folder = new TestFolder();
        folder.Write("claude.cmd");
        var native = folder.Write("node_modules/@anthropic-ai/claude-code/bin/claude.exe");
        var resolution = ExecutableLocator.Find(ProviderKind.Claude, folder.Path);
        Assert.Equal(native, resolution!.FileName);
        Assert.Empty(resolution.PrefixArguments);
    }

    [Fact]
    public void KnownNodeScriptLayoutPassesTheScriptAsASeparateArgument()
    {
        using var folder = new TestFolder();
        folder.Write("codex.cmd");
        var script = folder.Write("node_modules/@openai/codex/bin/codex.js");
        var node = folder.Write("node.exe");
        var resolution = ExecutableLocator.Find(ProviderKind.Codex, folder.Path);
        Assert.Equal(node, resolution!.FileName);
        Assert.Equal(script, Assert.Single(resolution.PrefixArguments));
    }

    [Fact]
    public async Task MissingExecutableAndUnknownWrapperHaveDistinctDiagnoses()
    {
        using var folder = new TestFolder();
        var probe = new ProviderInstallationProbe(folder.Path);
        Assert.Equal(InstallationStatus.Missing, (await probe.InspectAsync(ProviderKind.Codex, folder.Path)).Status);
        folder.Write("codex.cmd");
        Assert.Equal(InstallationStatus.Unsupported, (await probe.InspectAsync(ProviderKind.Codex, folder.Path)).Status);
    }

    [Fact]
    public async Task LaunchFailureDoesNotLeakStderrOrConfiguration()
    {
        using var folder = new TestFolder();
        folder.Write("codex.exe");
        var report = await new ProviderInstallationProbe(folder.Path).InspectAsync(ProviderKind.Codex, folder.Path);
        Assert.Equal(InstallationStatus.Failed, report.Status);
        Assert.Null(report.Version);
        Assert.DoesNotContain(folder.Path, report.Diagnostic);
    }

    private sealed class TestFolder : IDisposable
    {
        private readonly string _root = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
        public string Path { get; }
        public TestFolder()
        {
            Path = System.IO.Path.Combine(_root, "sintonia-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Write(string relative)
        {
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relative));
            if (!path.StartsWith(Path + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Arquivo de teste, sem shell executável.");
            return path;
        }
        public void Dispose()
        {
            if (!System.IO.Path.GetFullPath(Path).StartsWith(System.IO.Path.Combine(_root, "sintonia-tests-"), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Diretório de teste fora do escopo.");
            Directory.Delete(Path, recursive: true);
        }
    }
}
