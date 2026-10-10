using Sintonia.Core;

namespace Sintonia.Desktop.ViewModels;

public sealed class QuestionChoiceViewModel(ConversationQuestionChoice choice, bool multiple, string group, Action<QuestionChoiceViewModel> changed) : ObservableObject
{
    private bool _selected;
    public string Label => choice.Label;
    public string Description => choice.Description;
    public string? Preview => choice.Preview;
    public bool HasPreview => !string.IsNullOrEmpty(Preview);
    public bool MultiSelect => multiple;
    public string Group => group;
    public bool IsSelected { get => _selected; set { if (Set(ref _selected, value)) changed(this); } }
}

public sealed class QuestionItemViewModel : ObservableObject
{
    private readonly ConversationQuestion _question;
    private readonly Action _changed;
    private string _customText = "";
    public QuestionItemViewModel(ConversationQuestion question, Action changed)
    {
        _question = question; _changed = changed;
        var group = "question-" + Guid.NewGuid().ToString("N");
        Choices = question.Choices.Select(choice => new QuestionChoiceViewModel(choice, question.MultiSelect, group, ChoiceChanged)).ToArray();
    }
    public string Text => _question.Text;
    public string Header => _question.Header;
    public string Hint => _question.MultiSelect ? "Escolha uma ou mais opções, ou escreva outra resposta." : "Escolha uma opção, ou escreva outra resposta.";
    public IReadOnlyList<QuestionChoiceViewModel> Choices { get; }
    public string CustomText
    {
        get => _customText;
        set
        {
            if (!Set(ref _customText, value)) return;
            if (!string.IsNullOrEmpty(value)) foreach (var choice in Choices) choice.IsSelected = false;
            _changed();
        }
    }
    public ConversationQuestionAnswer GetAnswer() => new(Choices.Where(choice => choice.IsSelected).Select(choice => choice.Label).ToArray(), CustomText);
    private void ChoiceChanged(QuestionChoiceViewModel changed)
    {
        if (changed.IsSelected)
        {
            CustomText = "";
            if (!_question.MultiSelect) foreach (var choice in Choices.Where(choice => choice != changed)) choice.IsSelected = false;
        }
        _changed();
    }
}

public sealed class ConversationQuestionsViewModel : ObservableObject
{
    private readonly ConversationQuestionRequest _request;
    private readonly Action<ConversationQuestionResponse?> _complete;
    private bool _finished;
    public ConversationQuestionsViewModel(ConversationQuestionRequest request, string context, Action<ConversationQuestionResponse?> complete)
    {
        if (!ConversationQuestionPolicy.IsValid(request)) throw new ArgumentException("Perguntas inválidas.");
        _request = request; _complete = complete; Context = context;
        Questions = request.Questions.Select(question => new QuestionItemViewModel(question, Refresh)).ToArray();
        SubmitCommand = new(() => { if (CanSubmit) Complete(GetResponse()); }, () => CanSubmit);
        DeclineCommand = new(() => Complete(null), () => IsActive);
    }
    public string Context { get; }
    public string WorkingDirectory => _request.WorkingDirectory;
    public IReadOnlyList<QuestionItemViewModel> Questions { get; }
    public bool IsActive => !_finished;
    public bool CanSubmit => !_finished && ConversationQuestionPolicy.TryGetAnswers(_request, GetResponse(), out _);
    public DelegateCommand SubmitCommand { get; }
    public DelegateCommand DeclineCommand { get; }
    public void Withdraw() { _finished = true; Refresh(); }
    private ConversationQuestionResponse GetResponse() => new(Questions.Select(question => question.GetAnswer()).ToArray());
    private void Complete(ConversationQuestionResponse? response)
    {
        if (_finished) return;
        _finished = true; Refresh(); _complete(response);
    }
    private void Refresh() { Notify(nameof(CanSubmit)); Notify(nameof(IsActive)); SubmitCommand?.Refresh(); DeclineCommand?.Refresh(); }
}
