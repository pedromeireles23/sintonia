using Sintonia.Core;

namespace Sintonia.Core.Tests;

public sealed class ConversationQuestionTests
{
    private static ConversationQuestionRequest Request(bool multiple = false) => new(ProviderKind.Claude, AppContext.BaseDirectory,
        [new("Qual formato?", "Formato", multiple, [new("Resumo", "Texto curto."), new("Detalhes", "Texto completo.")])]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnswersUseExactQuestionAndLabelsAndPreserveLiteralFreeText(bool multiple)
    {
        var request = Request(multiple);
        Assert.True(ConversationQuestionPolicy.TryGetAnswers(request, new([new(["Resumo"])]), out var answers));
        if (multiple) Assert.Equal(new[] { "Resumo" }, Assert.IsType<string[]>(answers["Qual formato?"]));
        else Assert.Equal("Resumo", answers["Qual formato?"]);
        var text = "  ação \"literal\"\n$(não executar) &| ";
        Assert.True(ConversationQuestionPolicy.TryGetAnswers(request, new([new([], text)]), out answers));
        Assert.Equal(text, answers["Qual formato?"]);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("single-multiple")]
    [InlineData("text-and-choice")]
    [InlineData("blank-text")]
    [InlineData("long-text")]
    [InlineData("extra-answer")]
    public void RejectsIncompleteAmbiguousOrUnshownAnswers(string mode)
    {
        var answer = mode switch
        {
            "missing" => new ConversationQuestionAnswer([]),
            "unknown" => new(["Not shown"]), "duplicate" => new(["Resumo", "Resumo"]),
            "single-multiple" => new(["Resumo", "Detalhes"]), "text-and-choice" => new(["Resumo"], "Outro"),
            "blank-text" => new([], " \n"), "long-text" => new([], new string('a', 4001)), _ => new(["Resumo"])
        };
        Assert.False(ConversationQuestionPolicy.TryGetAnswers(Request(), new(mode == "extra-answer" ? [answer, answer] : [answer]), out _));
    }

    [Theory]
    [InlineData("duplicate-question")]
    [InlineData("duplicate-label")]
    [InlineData("long-header")]
    [InlineData("empty-label")]
    [InlineData("too-many")]
    public void RejectsInvalidQuestionIdentityAndBounds(string mode)
    {
        var request = Request(); var question = request.Questions[0];
        request = mode switch
        {
            "duplicate-question" => request with { Questions = [question, question] },
            "duplicate-label" => request with { Questions = [question with { Choices = [question.Choices[0], question.Choices[0]] }] },
            "long-header" => request with { Questions = [question with { Header = new string('a', 13) }] },
            "empty-label" => request with { Questions = [question with { Choices = [question.Choices[0] with { Label = " " }, question.Choices[1]] }] },
            _ => request with { Questions = [question, question, question, question, question] }
        };
        Assert.False(ConversationQuestionPolicy.IsValid(request));
    }
}
