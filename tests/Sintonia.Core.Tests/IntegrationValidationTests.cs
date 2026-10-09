using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class IntegrationValidationTests
{
    private static TaskIntegrationValidation Intent()
    {
        var root = Path.Combine(Path.GetTempPath(), "sintonia-validation-domain", Guid.NewGuid().ToString("N"));
        var task = Guid.NewGuid(); var original = Path.Combine(root, "projeto"); var common = Path.Combine(original, ".git");
        var worktree = new TaskWorktree(task.ToString(), original, common, Path.Combine(root, "worktrees", task.ToString("N")), ".",
            new('a', 40), "codex/sintonia/" + task.ToString("N"), TaskWorktreeState.Ready);
        var delivery = new TaskDelivery(task.ToString(), Guid.NewGuid().ToString(), worktree, new('b', 40), new('c', 40), DateTimeOffset.UtcNow);
        var target = new TaskIntegrationTarget(original, common, "refs/heads/main", new('a', 40));
        var first = new TaskIntegrationReservation(Guid.NewGuid().ToString(), delivery, target, DateTimeOffset.UtcNow);
        var preparation = new TaskIntegrationPreparation(first, Path.Combine(root, "combinations", Guid.Parse(first.Id).ToString("N")), TaskIntegrationPreparationState.Combined, new('d', 40));
        var project = Guid.NewGuid().ToString();
        return new(project, new(Guid.NewGuid().ToString(), delivery, target, DateTimeOffset.UtcNow), preparation,
            new(project, 1, [new("Conferir documentação", Path.Combine(root, "validator.exe"), []), new("Conferir links", Path.Combine(root, "validator.exe"), ["links"])]), DateTimeOffset.UtcNow);
    }
    private static ValidationCommandResult Passed(TaskIntegrationValidation intent, int index) =>
        new(index, ValidationState.Passed, 0, "conferido", "", false, intent.StartedAt, intent.StartedAt.AddSeconds(1));

    [Fact]
    public void PassingEvidenceRequiresEveryConfiguredCommandInOrderAndSuccessfulExitCodes()
    {
        var intent = Intent(); intent.ValidateDefinition();
        var passed = intent with { State = ValidationState.Passed, FinishedAt = intent.StartedAt.AddSeconds(2), Results = [Passed(intent, 0), Passed(intent, 1)] };
        passed.ValidateDefinition();
        Assert.Throws<ArgumentException>(() => (passed with { Results = [Passed(intent, 0)] }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (passed with { Results = [Passed(intent, 1), Passed(intent, 0)] }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (passed with { Results = [Passed(intent, 0), Passed(intent, 1) with { ExitCode = 7 }] }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (passed with { Configuration = passed.Configuration with { Commands = [] } }).ValidateDefinition());
    }

    [Fact]
    public void FailedCancelledAndInterruptedRecordsNeverBecomePassingEvidence()
    {
        var intent = Intent(); var finished = intent.StartedAt.AddSeconds(2);
        var failure = intent with { State = ValidationState.Failed, FinishedAt = finished, Error = "comando falhou", Results = [Passed(intent, 0) with { State = ValidationState.Failed, ExitCode = 7 }] };
        failure.ValidateDefinition();
        Assert.Throws<ArgumentException>(() => (failure with { State = ValidationState.Passed, Error = null }).ValidateDefinition());
        (intent with { State = ValidationState.Cancelled, FinishedAt = finished, Error = "cancelado", Results = [Passed(intent, 0)] }).ValidateDefinition();
        (intent with { State = ValidationState.Interrupted, FinishedAt = finished, Error = "interrompido" }).ValidateDefinition();
        Assert.Throws<ArgumentException>(() => (intent with { State = ValidationState.Interrupted, FinishedAt = finished }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (failure with { Results = [Passed(intent, 0) with { State = ValidationState.Failed, ExitCode = 0 }] }).ValidateDefinition());
    }

    [Fact]
    public void EvidenceCannotChangeItsProjectTreeDeliveryOrCommandTimeBounds()
    {
        var intent = Intent();
        Assert.Throws<ArgumentException>(() => (intent with { ProjectId = Guid.NewGuid().ToString() }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (intent with { Preparation = intent.Preparation with { State = TaskIntegrationPreparationState.Conflicted, Tree = null, Conflicts = ["arquivo.txt"] } }).ValidateDefinition());
        Assert.Throws<ArgumentException>(() => (intent with { Reservation = intent.Reservation with { Delivery = intent.Reservation.Delivery with { Commit = new('e', 40) } } }).ValidateDefinition());
        var ended = intent with { State = ValidationState.NeedsAttention, Error = "conteúdo alterado", FinishedAt = intent.StartedAt.AddSeconds(2), Results = [Passed(intent, 0)] };
        ended.ValidateDefinition();
        Assert.Throws<ArgumentException>(() => (ended with { Results = [Passed(intent, 0) with { FinishedAt = intent.StartedAt.AddSeconds(3) }] }).ValidateDefinition());
    }
}
