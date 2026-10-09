using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class WorkspaceExecutionPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public void ParallelWritingRequiresDistinctIsolatedCheckouts(bool aIsolated, bool bIsolated, bool expected)
    {
        var project = Guid.NewGuid().ToString();
        var a = new WorkspaceExecutionSlot("a", project, ConversationAccess.WorkspaceWrite, Path.GetFullPath("checkout-a"), aIsolated);
        var b = new WorkspaceExecutionSlot("b", project, ConversationAccess.WorkspaceWrite, Path.GetFullPath("checkout-b"), bIsolated);
        Assert.Equal(expected, WorkspaceExecutionPolicy.CanAdmit(b, [a], 7, out _));
        Assert.False(WorkspaceExecutionPolicy.CanAdmit(b with { Directory = a.Directory }, [a], 7, out _));
    }

    [Fact]
    public void ChiefMayReadOriginalWhileSeparateTaskWritesButOverlapAcrossProjectsIsBlocked()
    {
        var project = new WorkspaceProject(Guid.NewGuid().ToString(), "Projeto", Path.GetFullPath("original"));
        var chief = new WorkspaceConversation("chief", project.Id, "Plano", ProviderKind.Claude, null, null,
            PlanProposalFormat.ChiefFunctionName, "", ConversationAccess.WorkspaceWrite);
        var reader = WorkspaceExecutionSlot.Create(project, chief);
        Assert.Equal(ConversationAccess.ReadOnly, reader.Access);
        var writer = new WorkspaceExecutionSlot("writer", project.Id, ConversationAccess.WorkspaceWrite, Path.GetFullPath("checkout"), true);
        Assert.True(WorkspaceExecutionPolicy.CanAdmit(reader, [writer], 3, out _));
        Assert.False(WorkspaceExecutionPolicy.CanAdmit(reader with { ProjectId = "other", Directory = Path.Combine(writer.Directory, "src") }, [writer], 7, out _));
    }
}
