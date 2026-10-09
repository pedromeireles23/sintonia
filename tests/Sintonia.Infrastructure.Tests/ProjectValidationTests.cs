using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sintonia.Core;
using Sintonia.Infrastructure.Persistence;
using Sintonia.Infrastructure.Validation;

namespace Sintonia.Infrastructure.Tests;

[Collection("Git environment")]
public sealed class ProjectValidationTests
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "fixtures", "Sintonia.ProcessFixture.exe");
    internal static ProjectValidationCommand Command(params string[] arguments) => new("Conferir projeto", Executable, arguments, ".", 10);

    [Theory]
    [InlineData("../fora")]
    [InlineData("C:\\fora")]
    [InlineData("portal/../../fora")]
    [InlineData("portal/.Git")]
    [InlineData("portal//pasta")]
    public void WorkingDirectoryMustStayRelativeToCombination(string directory) =>
        Assert.Throws<ArgumentException>(() => (Command("echo") with { WorkingDirectory = directory }).ValidateDefinition());

    [Fact]
    public void ConfigurationRejectsWrappersAmbiguousArgumentsAndUnboundedCommands()
    {
        Assert.Throws<ArgumentException>(() => (Command("echo") with { Executable = "dotnet.exe" }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (Command("echo") with { Executable = Executable.Replace(".exe", ".cmd", StringComparison.Ordinal) }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (Command("echo") with { Arguments = ["nul\0"] }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (Command("echo") with { TimeoutSeconds = 601 }).ValidateDefinition());
        var configuration = new ProjectValidationConfiguration(Guid.NewGuid().ToString(), 0, [Command("echo"), Command("fail")]);
        Assert.Throws<ArgumentException>(configuration.ValidateDefinition);
        var arguments = new List<string> { "echo", "literal" }; var commands = new List<ProjectValidationCommand> { Command() with { Arguments = arguments } };
        var snapshot = (configuration with { Commands = commands }).Snapshot(); arguments.Clear(); commands.Clear();
        Assert.Single(snapshot.Commands); Assert.Equal(["echo", "literal"], snapshot.Commands[0].Arguments);
    }

    [Fact]
    public async Task ConfigurationPreservesArgumentsPerProjectAndRejectsStaleRevisionAfterReopening()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var store = new SqliteWorkspaceStore(fixture.Database); await store.InitializeAsync();
        var first = await store.AddProjectAsync(fixture.Root); var directory = Path.Combine(fixture.Root, "outro"); Directory.CreateDirectory(directory);
        var second = await store.AddProjectAsync(directory); var empty = await store.GetProjectValidationAsync(first.Id);
        Assert.Empty(empty.Commands); Assert.Equal(0, empty.Revision);
        string[] arguments = ["echo", "ação com espaços", "$(literal)", "&|;%PATH%"];
        var saved = await store.SaveProjectValidationAsync(empty with { Commands = [Command(arguments)] });
        Assert.Equal(1, saved.Revision); Assert.Empty((await store.GetProjectValidationAsync(second.Id)).Commands);
        var reopened = new SqliteWorkspaceStore(fixture.Database); await reopened.InitializeAsync();
        Assert.Equal(JsonSerializer.Serialize(saved), JsonSerializer.Serialize(await reopened.GetProjectValidationAsync(first.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.SaveProjectValidationAsync(empty));
        var cleared = await reopened.SaveProjectValidationAsync(saved with { Commands = [] }); Assert.Equal(2, cleared.Revision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.SaveProjectValidationAsync(saved));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.GetProjectValidationAsync(Guid.NewGuid().ToString()));
    }

    [Fact]
    public async Task RunnerUsesLiteralArgumentsAndConfiguredSubfolderWithGitEnvironmentRemoved()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var directory = Path.Combine(fixture.Root, "documentação"); Directory.CreateDirectory(directory);
        string[] literals = ["ação com espaços", "$(literal)", "`backtick`", "&|;%PATH%"];
        var old = Environment.GetEnvironmentVariable("GIT_INDEX_FILE"); var oldProtocol = Environment.GetEnvironmentVariable("GIT_ALLOW_PROTOCOL");
        try
        {
            Environment.SetEnvironmentVariable("GIT_INDEX_FILE", "índice herdado");
            Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", "herdado");
            var result = await new ValidationCommandRunner().RunAsync(Command(["validation-context", .. literals]) with { WorkingDirectory = "documentação" }, 0, fixture.Root, CancellationToken.None);
            Assert.Equal(ValidationState.Passed, result.State); result.ValidateDefinition();
            using var json = JsonDocument.Parse(result.StandardOutput); Assert.Equal(directory, json.RootElement.GetProperty("Directory").GetString());
            Assert.Equal(literals, json.RootElement.GetProperty("Arguments").EnumerateArray().Select(v => v.GetString()));
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("GitIndex").ValueKind);
            Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("GitProtocol").ValueKind);
        }
        finally { Environment.SetEnvironmentVariable("GIT_INDEX_FILE", old); Environment.SetEnvironmentVariable("GIT_ALLOW_PROTOCOL", oldProtocol); }
    }

    [Fact]
    public async Task RunnerRetainsFailureAndBoundedOutputWithoutPipeDeadlock()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var runner = new ValidationCommandRunner();
        var failed = await runner.RunAsync(Command("fail"), 0, fixture.Root, CancellationToken.None);
        Assert.Equal(ValidationState.Failed, failed.State); Assert.Equal(7, failed.ExitCode); Assert.Contains("Falha de teste", failed.StandardError);
        var flood = await runner.RunAsync(Command("flood"), 1, fixture.Root, CancellationToken.None);
        Assert.Equal(ValidationState.Passed, flood.State); Assert.True(flood.Truncated); Assert.Equal(65536, flood.StandardOutput.Length); Assert.Equal(65536, flood.StandardError.Length);
        failed.ValidateDefinition(); flood.ValidateDefinition();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutAndCancellationRetainLogsAndStopChildProcess(bool cancel)
    {
        using var fixture = new TaskWorktreeTests.Fixture(); using var stop = new CancellationTokenSource();
        if (cancel) stop.CancelAfter(TimeSpan.FromSeconds(2));
        var result = await new ValidationCommandRunner().RunAsync(Command("child") with { TimeoutSeconds = cancel ? 10 : 2 }, 0, fixture.Root, stop.Token);
        Assert.Equal(cancel ? ValidationState.Cancelled : ValidationState.TimedOut, result.State); result.ValidateDefinition();
        var id = int.Parse(result.StandardOutput.Split('\n').Single(s => s.StartsWith("CHILD:", StringComparison.Ordinal))[6..]);
        try { using var child = Process.GetProcessById(id); Assert.True(child.HasExited || child.WaitForExit(3000)); }
        catch (ArgumentException) { }
    }

    [Fact]
    public async Task RootExitCannotLeaveItsChildRunningAfterTheValidationReturns()
    {
        using var fixture = new TaskWorktreeTests.Fixture();
        var result = await new ValidationCommandRunner().RunAsync(Command("orphan-child"), 0, fixture.Root, CancellationToken.None);
        Assert.Equal(ValidationState.Passed, result.State); Assert.Equal(0, result.ExitCode);
        var id = int.Parse(result.StandardOutput.Split('\n').Single(s => s.StartsWith("CHILD:", StringComparison.Ordinal))[6..]);
        try { using var child = Process.GetProcessById(id); Assert.True(child.HasExited, "A validação retornou com um filho ainda ativo."); }
        catch (ArgumentException) { }
    }

    [Fact]
    public async Task MissingExecutableOrDirectoryDoesNotLaunchACommand()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var runner = new ValidationCommandRunner();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Command("echo") with { Executable = Path.Combine(fixture.Root, "ausente.exe") }, 0, fixture.Root, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(Command("echo") with { WorkingDirectory = "ausente" }, 0, fixture.Root, CancellationToken.None));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(Command("echo"), 0, fixture.Root, cancelled.Token));
    }

    [Fact]
    public async Task Schema7MigrationPreservesProjectAndStartsWithNoCommands()
    {
        using var fixture = new TaskWorktreeTests.Fixture(); var store = new SqliteWorkspaceStore(fixture.Database); await store.InitializeAsync(); var project = await store.AddProjectAsync(fixture.Root);
        using (var connection = new SqliteConnection("Data Source=" + fixture.Database + ";Pooling=False"))
        { connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE project_validations; PRAGMA user_version=7;"; command.ExecuteNonQuery(); }
        await store.InitializeAsync(); Assert.Equal(project, (await store.GetProjectsAsync()).Single()); Assert.Empty((await store.GetProjectValidationAsync(project.Id)).Commands);
    }
}
