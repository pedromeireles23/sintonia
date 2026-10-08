using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class PlanProposalTests
{
    private static ProposedTask Task(string id = "task-1", params string[] dependencies) => new(id, "Criar menu",
        "Interface", ProviderKind.Codex, null, ConversationAccess.WorkspaceWrite, "Implementar menu com título e botão.",
        ["src/"], dependencies, ["Botão abre o jogo."]);
    private static PlanProposal Plan(params ProposedTask[] tasks) => new(1, "Menu inicial", "Entrar no jogo pelo menu.", tasks.Length == 0 ? [Task()] : tasks);
    private static string Response(PlanProposal proposal) => "Plano para revisão:\n```sintonia-plan\n" + PlanProposalFormat.Serialize(proposal) + "\n```\nConfira as entregas.";

    [Fact]
    public void ReadsOneBoundedProposalWithBothProvidersAndLiteralInstructions()
    {
        var definition = Plan(Task(), Task("task-2", "task-1") with { Provider = ProviderKind.Claude,
            Access = ConversationAccess.ReadOnly, Model = "modelo-verificado", Instructions = "Revisar $(literal) &| sem executar texto." });
        Assert.True(PlanProposalFormat.TryParseResponse(Response(definition).Replace("\n", "\r\n"), out var parsed, out var error));
        Assert.Empty(error);
        Assert.Equal(PlanProposalFormat.Serialize(definition), PlanProposalFormat.Serialize(parsed!));
        Assert.Equal(ProviderKind.Claude, parsed!.Tasks[1].Provider);
    }

    [Theory]
    [InlineData("sem proposta")]
    [InlineData("```json\n{}\n```")]
    [InlineData("```sintonia-plan\n{}")]
    [InlineData("```sintonia-plan\n{}\n```extra")]
    [InlineData("prefixo```sintonia-plan\n{}\n```")]
    public void RefusesFreeTextIncompleteOrUnmarkedPlans(string response)
    {
        Assert.False(PlanProposalFormat.TryParseResponse(response, out var proposal, out var error));
        Assert.Null(proposal); Assert.NotEmpty(error);
    }

    [Fact]
    public void RefusesTwoProposalsInsteadOfChoosingOneSilently()
    {
        Assert.False(PlanProposalFormat.TryParseResponse(Response(Plan()) + "\n" + Response(Plan()), out _, out _));
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("unknown-provider")]
    [InlineData("numeric-provider")]
    [InlineData("unknown-access")]
    [InlineData("missing-provider")]
    public void RejectsAmbiguousMissingOrUnsupportedFields(string defect)
    {
        var json = PlanProposalFormat.Serialize(Plan());
        json = defect switch
        {
            "unknown-field" => json.Insert(1, "\"execute\":true,"),
            "duplicate-field" => json.Insert(1, "\"schemaVersion\":1,"),
            "unknown-provider" => json.Replace("\"Codex\"", "\"External\"", StringComparison.Ordinal),
            "numeric-provider" => json.Replace("\"Codex\"", "0", StringComparison.Ordinal),
            "unknown-access" => json.Replace("\"WorkspaceWrite\"", "\"Bypass\"", StringComparison.Ordinal),
            _ => json.Replace("\"provider\": \"Codex\",", "", StringComparison.Ordinal)
        };
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.ParseJson(json));
    }

    [Theory]
    [InlineData("duplicate-id")]
    [InlineData("unknown-dependency")]
    [InlineData("self-dependency")]
    [InlineData("duplicate-dependency")]
    [InlineData("cycle")]
    public void RejectsInvalidDependencyGraph(string defect)
    {
        var tasks = defect switch
        {
            "duplicate-id" => new[] { Task(), Task() },
            "unknown-dependency" => [Task("task-1", "missing")],
            "self-dependency" => [Task("task-1", "task-1")],
            "duplicate-dependency" => [Task(), Task("task-2", "task-1", "task-1")],
            _ => [Task("task-1", "task-2"), Task("task-2", "task-1")]
        };
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan(tasks)));
    }

    [Theory]
    [InlineData("C:/outside.txt")]
    [InlineData("/outside.txt")]
    [InlineData("\\\\server\\share")]
    [InlineData("../outside.txt")]
    [InlineData("src/../outside.txt")]
    [InlineData("src\\..\\outside.txt")]
    [InlineData("src/*.cs")]
    [InlineData("src/file:stream")]
    [InlineData("src/NUL.txt")]
    [InlineData("src/COM1")]
    [InlineData("src/name.")]
    public void ScopeRejectsExternalTraversalWildcardAndInvalidWindowsPaths(string path)
    {
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan(Task() with { Scope = [path] })));
    }

    [Theory]
    [InlineData("src/menu.cs")]
    [InlineData("src\\menu.cs")]
    [InlineData("arte ação/menu.png")]
    [InlineData(".")]
    public void ScopeAllowsRelativeFilesAndExplicitProjectScope(string path)
    {
        PlanProposalFormat.Validate(Plan(Task() with { Scope = [path] }));
    }

    [Fact]
    public void LimitsTaskCountTextAndRequiredDeliveryCriteria()
    {
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan() with { Tasks = [] }));
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan(Enumerable.Range(0, 21).Select(i => Task("t" + i)).ToArray())));
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan(Task() with { Instructions = new string('x', 8001) })));
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.Validate(Plan(Task() with { AcceptanceCriteria = [] })));
        Assert.Throws<PlanValidationException>(() => PlanProposalFormat.ParseJson(new string(' ', 128001)));
        Assert.False(PlanProposalFormat.TryParseResponse(new string('x', 256001), out _, out _));
    }
}
