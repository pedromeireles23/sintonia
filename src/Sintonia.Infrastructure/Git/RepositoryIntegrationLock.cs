using System.Security.Cryptography;
using System.Text;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Git;

/// <summary>File sharing locks survive async continuations and are released by the OS on process exit.</summary>
public sealed class RepositoryIntegrationLock : IRepositoryIntegrationLock
{
    internal static string LockFile(string commonGitDirectory)
    {
        if (!Path.IsPathFullyQualified(commonGitDirectory)) throw new ArgumentException("Diretório Git comum inválido.");
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(commonGitDirectory)).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "integration-locks", hash + ".lock");
    }

    public IDisposable Acquire(string commonGitDirectory) => TryAcquire(commonGitDirectory)
        ?? throw new InvalidOperationException("Há uma operação de integração ativa neste repositório. Aguarde seu término.");

    internal static IDisposable? TryAcquire(string commonGitDirectory)
    {
        var file = LockFile(commonGitDirectory);
        GitTaskWorktreeManager.CheckPath(file);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        GitTaskWorktreeManager.CheckPath(file);
        try { return new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return null; }
        // Never delete the marker: deleting/recreating it could let two processes lock different files.
    }
}
