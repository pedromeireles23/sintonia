using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class WorkspaceTaskPolicyTests
{
    [Fact]
    public void RequestedAdjustmentSurvivesAFailedAttemptAndDependencyContextIsBounded()
    {
        var definition = new ProposedTask("work", "Documentar", "Documentação", ProviderKind.Codex, null, ConversationAccess.ReadOnly,
            "Produzir relatório.", ["docs/"], ["source"], ["Fontes identificadas."]);
        var dependency = new WorkspaceTask("dependency", "batch", definition with { Id = "source", Dependencies = [] }, "source-chat",
            WorkspaceTaskState.Approved, 1, "approved-run", new string('b', 4000));
        var task = new WorkspaceTask("task", "batch", definition, "chat", WorkspaceTaskState.Failed, 2, "failed-run", "Manter fontes no relatório.");
        var batch = new WorkspaceTaskBatch("batch", "project", "proposal", 1, new(1, "Documentação", "Relatório.", [definition, dependency.Definition]), [dependency, task]);
        var run = new ChatRun("approved-run", "source-chat", "pedido", new string('a', 256000), ChatRunState.Completed, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);
        var prompt = WorkspaceTaskPolicy.BuildPrompt(batch, task, [run]);
        Assert.Contains("adjustment", prompt); Assert.Contains("Manter fontes", prompt); Assert.Contains("approved-run", prompt);
        Assert.True(prompt.Length < 12000); Assert.DoesNotContain(new string('a', 5000), prompt);
        Assert.False(WorkspaceTaskPolicy.CanStart(task with { Attempts = 3 }, batch.Tasks));
    }
}
