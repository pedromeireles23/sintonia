namespace Sintonia.Core;

public sealed record ConversationQuestionChoice(string Label, string Description, string? Preview = null);
public sealed record ConversationQuestion(string Text, string Header, bool MultiSelect, IReadOnlyList<ConversationQuestionChoice> Choices);
public sealed record ConversationQuestionRequest(ProviderKind Provider, string WorkingDirectory, IReadOnlyList<ConversationQuestion> Questions);
public sealed record ConversationQuestionAnswer(IReadOnlyList<string> SelectedLabels, string? CustomText = null);
public sealed record ConversationQuestionResponse(IReadOnlyList<ConversationQuestionAnswer> Answers);

/// <summary>Answers address the exact questions/options received; no implicit selections or permission changes.</summary>
public static class ConversationQuestionPolicy
{
    public static bool IsValid(ConversationQuestionRequest request)
    {
        if (!Enum.IsDefined(request.Provider) || string.IsNullOrWhiteSpace(request.WorkingDirectory)
            || request.Questions is null || request.Questions.Count is < 1 or > 4) return false;
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in request.Questions)
        {
            if (question is null || !Text(question.Text, 2000) || !texts.Add(question.Text)
                || !Text(question.Header, 12) || question.Header.Any(char.IsControl)
                || question.Choices is null || question.Choices.Count is < 2 or > 4) return false;
            var labels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var choice in question.Choices)
                if (choice is null || !Text(choice.Label, 200) || !labels.Add(choice.Label)
                    || !Text(choice.Description, 2000) || choice.Preview?.Length > 10_000) return false;
        }
        return true;
    }

    public static bool TryGetAnswers(ConversationQuestionRequest request, ConversationQuestionResponse? response,
        out IReadOnlyDictionary<string, object> answers)
    {
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        answers = values;
        if (!IsValid(request) || response?.Answers is null || response.Answers.Count != request.Questions.Count) return false;
        for (var index = 0; index < request.Questions.Count; index++)
        {
            var question = request.Questions[index]; var answer = response.Answers[index];
            if (answer?.SelectedLabels is null) return false;
            if (!string.IsNullOrEmpty(answer.CustomText))
            {
                if (!Text(answer.CustomText, 4000) || answer.SelectedLabels.Count != 0) return false;
                values.Add(question.Text, answer.CustomText);
            }
            else
            {
                var selected = answer.SelectedLabels;
                if (selected.Count < 1 || selected.Count > question.Choices.Count || !question.MultiSelect && selected.Count != 1
                    || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count
                    || selected.Any(label => !question.Choices.Any(choice => choice.Label == label))) return false;
                values.Add(question.Text, question.MultiSelect ? selected.ToArray() : selected[0]);
            }
        }
        return true;
    }

    private static bool Text(string? text, int limit) => !string.IsNullOrWhiteSpace(text) && text.Length <= limit;
}
