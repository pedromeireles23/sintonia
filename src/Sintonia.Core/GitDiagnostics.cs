namespace Sintonia.Core;

public enum GitDiagnosticState { Available, NotRepository, GitUnavailable, BareRepository, Failed }

public sealed record GitFileChange(string Path, string? OriginalPath, char IndexStatus, char WorktreeStatus,
    bool IsConflict, bool IsUntracked, string SubmoduleState);

public sealed record GitRepositoryDiagnostic(string ProjectDirectory, GitDiagnosticState State, string? RootDirectory,
    string? Branch, string? HeadCommit, bool IsDetached, bool IsUnborn, string? Upstream, int? Ahead, int? Behind,
    IReadOnlyList<GitFileChange> Changes, DateTimeOffset CheckedAt, string Message);

public interface IGitRepositoryInspector
{
    Task<GitRepositoryDiagnostic> InspectAsync(string directory, CancellationToken cancellationToken = default);
}
