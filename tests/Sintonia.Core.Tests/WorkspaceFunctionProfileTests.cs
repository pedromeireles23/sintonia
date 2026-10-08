using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class WorkspaceFunctionProfileTests
{
    private static WorkspaceFunctionProfile Profile() => new("profile", "Documentação", "Documentação", ProviderKind.Claude, null, "", 0);

    [Theory]
    [InlineData("id")]
    [InlineData("revision")]
    [InlineData("name")]
    [InlineData("long-name")]
    [InlineData("function")]
    [InlineData("provider")]
    [InlineData("model")]
    [InlineData("instructions")]
    public void InvalidProfilesAreRejected(string field)
    {
        var profile = Profile();
        profile = field switch
        {
            "id" => profile with { Id = "" }, "revision" => profile with { Revision = -1 },
            "name" => profile with { Name = "nome\nquebrado" }, "long-name" => profile with { Name = new string('a', 81) },
            "function" => profile with { FunctionName = " " }, "provider" => profile with { Provider = (ProviderKind)99 },
            "model" => profile with { Model = new string('a', 121) }, _ => profile with { Instructions = new string('a', 8001) }
        };
        Assert.Throws<ArgumentException>(() => WorkspaceFunctionProfiles.Normalize(profile));
    }

    [Fact]
    public void NamesNormalizeButLiteralInstructionsRemainUntouched()
    {
        var literal = "  Instruções: `$(texto)`\nlinha literal  ";
        var profile = WorkspaceFunctionProfiles.Normalize(Profile() with { Name = "  Documentac\u0327a\u0303o  ", Model = "  ", Instructions = literal });
        Assert.Equal("Documentação", profile.Name); Assert.Null(profile.Model); Assert.Equal(literal, profile.Instructions);
        Assert.Equal(WorkspaceFunctionProfiles.NameKey("DOCUMENTAÇÃO"), WorkspaceFunctionProfiles.NameKey(profile.Name));
    }

    [Fact]
    public void ChiefLimitIncludesPlanningContractExactlyOnce()
    {
        var limit = PlanProposalFormat.ChiefAdditionalInstructionsLimit;
        var profile = Profile() with { FunctionName = PlanProposalFormat.ChiefFunctionName, Instructions = new string('a', limit) };
        WorkspaceFunctionProfiles.Normalize(profile);
        Assert.Equal(8000, PlanProposalFormat.ComposeChiefInstructions(profile.Instructions).Length);
        Assert.Throws<ArgumentException>(() => WorkspaceFunctionProfiles.Normalize(profile with { Instructions = profile.Instructions + "b" }));
        Assert.Throws<ArgumentException>(() => PlanProposalFormat.ComposeChiefInstructions(profile.Instructions + "b"));
    }
}
