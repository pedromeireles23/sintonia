using Sintonia.Core;
using Sintonia.Infrastructure.Diagnostics;

namespace Sintonia.Infrastructure.Git;

/// <summary>Only creates owned checkouts. Never removes, resets, forces, commits, fetches or merges.</summary>
public sealed class GitTaskWorktreeManager : IGitTaskWorktreeManager
{
    private readonly string _root;
    private readonly ExecutableLaunch? _launch;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    public GitTaskWorktreeManager(string? managedRoot = null, string? searchPath = null)
    {
        _root = Path.GetFullPath(managedRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Sintonia", "worktrees"));
        _launch = GitRepositoryInspector.Find(searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "");
    }

    public async Task<TaskWorktree> PlanAsync(WorkspaceProject project, string taskId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(taskId, out var id)) throw new ArgumentException("Identificador de tarefa inválido.");
        CheckPath(project.Directory);
        using var deadline = Deadline(cancellationToken);
        var type = await RunAsync(project.Directory, deadline.Token, "rev-parse", "--is-inside-work-tree", "--is-bare-repository").ConfigureAwait(false);
        if (type.ExitCode != 0 || type.StandardOutput.Replace("\r", "", StringComparison.Ordinal) != "true\nfalse\n")
            throw new InvalidOperationException("A preparação exige uma cópia de trabalho Git disponível. Confira a pasta e a confiança do repositório.");
        var rootResult = await RunAsync(project.Directory, deadline.Token, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        RequireSuccess(rootResult); var root = rootResult.StandardOutput.TrimEnd('\r', '\n'); CheckPath(root);
        var head = await RunAsync(project.Directory, deadline.Token, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
        if (head.ExitCode != 0) throw new InvalidOperationException("A preparação exige pelo menos um commit salvo no repositório.");
        var common = await CommonDirectoryAsync(root, deadline.Token).ConfigureAwait(false);
        var plan = new TaskWorktree(taskId, Path.GetFullPath(root), common, Path.Combine(_root, id.ToString("N")),
            Path.GetRelativePath(root, project.Directory), head.StandardOutput.TrimEnd('\r', '\n'), "codex/sintonia/" + id.ToString("N"));
        plan.ValidateDefinition(project.Directory);
        await CheckSourceAsync(plan, deadline.Token).ConfigureAwait(false);
        return plan;
    }

    public async Task PrepareAsync(TaskWorktree worktree, CancellationToken cancellationToken)
    {
        worktree.ValidateDefinition(); EnsureManagedLocation(worktree);
        using var deadline = Deadline(cancellationToken);
        await CheckSourceAsync(worktree, deadline.Token).ConfigureAwait(false);
        var entries = await ListAsync(worktree.RepositoryDirectory, deadline.Token).ConfigureAwait(false);
        var existing = entries.SingleOrDefault(e => SamePath(e.Directory, worktree.CheckoutDirectory));
        if (existing is not null)
        {
            await ValidateAsync(worktree, deadline.Token).ConfigureAwait(false);
            var status = await StatusWithoutFiltersAsync(worktree.CheckoutDirectory, deadline.Token).ConfigureAwait(false);
            RequireSuccess(status);
            if (existing.Head != worktree.BaseCommit || status.StandardOutput.Length != 0)
                throw new InvalidOperationException("A pasta já contém alterações. Confira os arquivos antes de retomar; o Sintonia não os sobrescreve.");
            return;
        }
        if (File.Exists(worktree.CheckoutDirectory) || Directory.Exists(worktree.CheckoutDirectory))
            throw new InvalidOperationException("A pasta de destino já existe e não corresponde a uma worktree registrada desta tarefa.");
        var branch = await RunAsync(worktree.RepositoryDirectory, deadline.Token, "show-ref", "--verify", "--quiet", "refs/heads/" + worktree.Branch).ConfigureAwait(false);
        if (branch.ExitCode != 1) throw new InvalidOperationException("A branch de destino existe ou não pôde ser consultada. Nenhuma branch foi sobrescrita.");
        Directory.CreateDirectory(_root); CheckPath(_root);
        var result = await RunAsync(worktree.RepositoryDirectory, deadline.Token, "worktree", "add", "--lock", "--reason", LockReason(worktree),
            "-b", worktree.Branch, worktree.CheckoutDirectory, worktree.BaseCommit).ConfigureAwait(false);
        RequireSuccess(result);
        await ValidateAsync(worktree, deadline.Token).ConfigureAwait(false);
    }

    public async Task ValidateAsync(TaskWorktree worktree, CancellationToken cancellationToken)
    {
        worktree.ValidateDefinition(); EnsureManagedLocation(worktree);
        CheckPath(worktree.WorkingDirectory);
        if (!Directory.Exists(worktree.WorkingDirectory)) throw new InvalidOperationException("A pasta da tarefa não está disponível. Confira a worktree antes de executar.");
        using var deadline = Deadline(cancellationToken);
        var root = await RunAsync(worktree.CheckoutDirectory, deadline.Token, "rev-parse", "--show-toplevel").ConfigureAwait(false);
        RequireSuccess(root);
        if (!SamePath(root.StandardOutput.TrimEnd('\r', '\n'), worktree.CheckoutDirectory))
            throw new InvalidOperationException("O Git redirecionou a pasta da tarefa. Confira a configuração antes de executar.");
        if (!SamePath(await CommonDirectoryAsync(worktree.CheckoutDirectory, deadline.Token).ConfigureAwait(false), worktree.CommonGitDirectory))
            throw new InvalidOperationException("A pasta da tarefa pertence a outro repositório Git.");
        var entry = (await ListAsync(worktree.CheckoutDirectory, deadline.Token).ConfigureAwait(false))
            .SingleOrDefault(e => SamePath(e.Directory, worktree.CheckoutDirectory));
        if (entry is null || entry.Branch != "refs/heads/" + worktree.Branch || entry.LockReason != LockReason(worktree) || entry.Prunable)
            throw new InvalidOperationException("O vínculo Git da tarefa mudou. Confira registro, branch e bloqueio da worktree antes de executar.");
        var ancestry = await RunAsync(worktree.CheckoutDirectory, deadline.Token, "merge-base", "--is-ancestor", worktree.BaseCommit, "HEAD").ConfigureAwait(false);
        if (ancestry.ExitCode != 0) throw new InvalidOperationException("A branch da tarefa não contém mais o commit de base registrado.");
    }

    private async Task CheckSourceAsync(TaskWorktree worktree, CancellationToken token)
    {
        EnsureManagedLocation(worktree); CheckPath(worktree.RepositoryDirectory); CheckPath(worktree.CommonGitDirectory);
        if (!SamePath(await CommonDirectoryAsync(worktree.RepositoryDirectory, token).ConfigureAwait(false), worktree.CommonGitDirectory))
            throw new InvalidOperationException("O repositório original mudou desde a confirmação. Confira a pasta.");
        // Inspect the pinned tree, not dirty attributes from the original checkout.
        // An installed LFS filter is harmless when no file actually uses it.
        await CheckTreeAsync(worktree.RepositoryDirectory, worktree.BaseCommit, token).ConfigureAwait(false);
        var tree = worktree.ProjectRelativeDirectory == "." ? worktree.BaseCommit + "^{tree}"
            : worktree.BaseCommit + ":" + worktree.ProjectRelativeDirectory.Replace('\\', '/');
        var project = await RunAsync(worktree.RepositoryDirectory, token, "cat-file", "-t", tree).ConfigureAwait(false);
        if (project.ExitCode != 0 || project.StandardOutput.Trim() != "tree")
            throw new InvalidOperationException("A pasta do projeto não existe no commit de base. Salve o projeto em Git antes de preparar.");
    }

    internal async Task CheckTreeAsync(string directory, string commit, CancellationToken token, bool forIntegration = false)
    {
        // The generic %(path) formatter quotes some names even with -z on installed Git.
        // Use the native default format, whose NUL records preserve the path verbatim.
        var files = await RunAsync(directory, token, "ls-tree", "--full-tree", "-r", "-z", commit).ConfigureAwait(false);
        RequireSuccess(files);
        if (files.StandardOutput.Length > 0 && !files.StandardOutput.EndsWith('\0')) throw new FormatException("Árvore Git incompleta.");
        var paths = new List<string>();
        foreach (var entry in files.StandardOutput.Split('\0').SkipLast(1))
        {
            var separator = entry.IndexOf('\t');
            if (separator < 8 || entry[6] != ' ') throw new FormatException("Árvore Git inválida.");
            if (entry.StartsWith("160000 ", StringComparison.Ordinal)) throw new InvalidOperationException("A preparação de worktrees com submódulos ainda não é suportada.");
            if (forIntegration && entry.StartsWith("120000 ", StringComparison.Ordinal))
                throw new InvalidOperationException("A combinação ainda não suporta links simbólicos versionados. Preserve os arquivos e confira o projeto.");
            paths.Add(entry[(separator + 1)..]);
        }
        for (var offset = 0; offset < paths.Count;)
        {
            var chunk = new List<string>(); var length = 0;
            while (offset < paths.Count && (chunk.Count == 0 || length + paths[offset].Length < 8000))
            { var path = paths[offset++]; chunk.Add(path); length += path.Length + 3; }
            var attributes = await RunAsync(directory, token,
                new[] { "check-attr", "--source=" + commit, "-z", "filter" }.Concat(forIntegration ? ["merge"] : Array.Empty<string>())
                    .Concat(["--"]).Concat(chunk).ToArray()).ConfigureAwait(false);
            RequireSuccess(attributes); var values = attributes.StandardOutput.Split('\0');
            var stride = forIntegration ? 6 : 3;
            if (values.Length != chunk.Count * stride + 1 || values[^1] != "") throw new FormatException("Atributos Git incompletos.");
            for (var i = 0; i < chunk.Count; i++)
            {
                if (values[i * stride] != chunk[i] || values[i * stride + 1] != "filter") throw new FormatException("Atributos Git inválidos.");
                if (values[i * stride + 2] is not ("unspecified" or "unset"))
                    throw new InvalidOperationException("A preparação ainda não suporta arquivos com filtros de checkout, como Git LFS. Nenhuma configuração foi alterada.");
                if (forIntegration && (values[i * stride + 3] != chunk[i] || values[i * stride + 4] != "merge"))
                    throw new FormatException("Atributos de merge Git inválidos.");
                if (forIntegration && values[i * stride + 5] is not ("unspecified" or "unset" or "set" or "text" or "binary" or "union"))
                    throw new InvalidOperationException("A combinação ainda não suporta drivers personalizados de merge. Nenhum driver foi executado.");
            }
        }
    }

    private void EnsureManagedLocation(TaskWorktree worktree)
    {
        if (!SamePath(Path.GetDirectoryName(worktree.CheckoutDirectory)!, _root))
            throw new InvalidOperationException("A worktree está fora da pasta gerenciada por esta instalação.");
        CheckPath(_root); CheckPath(worktree.CheckoutDirectory);
    }
    internal static void CheckPath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("A preparação não suporta links ou junções no caminho das pastas. Escolha um caminho direto.");
    }
    private static string LockReason(TaskWorktree worktree) => "Sintonia: tarefa " + worktree.TaskId;
    private async Task<ProcessProbeResult> StatusWithoutFiltersAsync(string directory, CancellationToken token)
        => await RunWithoutFiltersAsync(directory, token, ["status", "--porcelain=v2", "-z", "--untracked-files=all"]).ConfigureAwait(false);

    internal async Task<ProcessProbeResult> RunWithoutFiltersAsync(string directory, CancellationToken token,
        IReadOnlyList<string> command, bool allowTruncation = false)
    {
        // A dirty .gitattributes in an interrupted checkout must not execute a clean/process filter while checking retry safety.
        var keys = await RunAsync(directory, token, "config", "--name-only", "--get-regexp", "^filter\\..*\\.(clean|smudge|process)$").ConfigureAwait(false);
        if (keys.ExitCode is not (0 or 1)) RequireSuccess(keys);
        var arguments = new List<string>();
        foreach (var key in keys.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!key.StartsWith("filter.", StringComparison.OrdinalIgnoreCase) || key.LastIndexOf('.') <= 7)
                throw new FormatException("Nome de filtro Git inválido.");
            arguments.AddRange(["-c", key + "=", "-c", key[..key.LastIndexOf('.')] + ".required=false"]);
        }
        if (arguments.Sum(a => a.Length + 3) > 16000) throw new InvalidOperationException("Configuração de filtros extensa demais para uma conferência completa.");
        arguments.AddRange(command);
        return await RunProbeAsync(directory, token, allowTruncation, arguments.ToArray()).ConfigureAwait(false);
    }
    private static bool SamePath(string first, string second) => string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)), StringComparison.OrdinalIgnoreCase);
    private static CancellationTokenSource Deadline(CancellationToken token)
    { var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(Timeout); return deadline; }
    internal Task<ProcessProbeResult> RunAsync(string directory, CancellationToken token, params string[] arguments) =>
        RunProbeAsync(directory, token, false, arguments);
    private async Task<ProcessProbeResult> RunProbeAsync(string directory, CancellationToken token, bool allowTruncation, params string[] arguments)
    {
        if (_launch is null) throw new InvalidOperationException("Git não encontrado no PATH.");
        var result = await ProcessProbe.RunAsync(_launch,
            new[] { "--no-replace-objects", "--no-optional-locks", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
                "-c", "core.hooksPath=NUL" }.Concat(arguments).ToArray(),
            directory, Timeout, token, GitRepositoryInspector.CleanEnvironment()).ConfigureAwait(false);
        if (result.TimedOut) throw new TimeoutException("O Git excedeu o prazo de preparação. Confira possíveis efeitos na pasta.");
        if (result.Truncated && !allowTruncation) throw new InvalidOperationException("A saída Git excedeu o limite de 64 Ki caracteres. Confira a pasta antes de continuar.");
        return result;
    }
    private static void RequireSuccess(ProcessProbeResult result)
    {
        if (result.ExitCode != 0) throw new InvalidOperationException($"O Git recusou a operação (código {result.ExitCode}). Confira a pasta e a configuração; arquivos existentes foram preservados.");
    }
    private async Task<string> CommonDirectoryAsync(string directory, CancellationToken token)
    {
        var result = await RunAsync(directory, token, "rev-parse", "--path-format=absolute", "--git-common-dir").ConfigureAwait(false);
        RequireSuccess(result); var path = result.StandardOutput.TrimEnd('\r', '\n');
        if (!Path.IsPathFullyQualified(path) || !Directory.Exists(path)) throw new InvalidOperationException("Diretório Git comum indisponível.");
        return Path.GetFullPath(path);
    }
    private async Task<IReadOnlyList<GitWorktreeEntry>> ListAsync(string directory, CancellationToken token)
    {
        var result = await RunAsync(directory, token, "worktree", "list", "--porcelain", "-z").ConfigureAwait(false);
        RequireSuccess(result); return GitWorktreeListParser.Parse(result.StandardOutput);
    }
}
