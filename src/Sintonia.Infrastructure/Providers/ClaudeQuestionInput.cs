using System.Text.Json;
using Sintonia.Core;

namespace Sintonia.Infrastructure.Providers;

internal static class ClaudeQuestionInput
{
    public static bool TryParse(JsonElement input, string directory, out ConversationQuestionRequest request)
    {
        request = new(ProviderKind.Claude, directory, []);
        try
        {
            if (input.ValueKind != JsonValueKind.Object || input.GetRawText().Length > 64_000 || !UniqueProperties(input)) return false;
            var questions = new List<ConversationQuestion>();
            var array = input.GetProperty("questions");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 4) return false;
            foreach (var item in array.EnumerateArray())
            {
                var choices = new List<ConversationQuestionChoice>();
                var options = item.GetProperty("options");
                if (options.ValueKind != JsonValueKind.Array || options.GetArrayLength() is < 2 or > 4) return false;
                foreach (var option in options.EnumerateArray())
                    choices.Add(new(option.GetProperty("label").GetString()!, option.GetProperty("description").GetString()!,
                        option.TryGetProperty("preview", out var preview) && preview.ValueKind != JsonValueKind.Null ? preview.GetString() : null));
                var multiple = item.TryGetProperty("multiSelect", out var multi) && multi.GetBoolean();
                questions.Add(new(item.GetProperty("question").GetString()!, item.GetProperty("header").GetString()!, multiple, choices.AsReadOnly()));
            }
            request = new(ProviderKind.Claude, directory, questions.AsReadOnly());
            return ConversationQuestionPolicy.IsValid(request);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException) { return false; }
    }

    public static JsonElement WithAnswers(JsonElement input, IReadOnlyDictionary<string, object> answers)
    {
        var updated = input.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
        updated["answers"] = JsonSerializer.SerializeToElement(answers);
        return JsonSerializer.SerializeToElement(updated);
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }
}
