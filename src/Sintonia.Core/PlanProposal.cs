using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sintonia.Core;

public sealed record ProposedTask(string Id, string Title, string FunctionName, ProviderKind Provider, string? Model,
    ConversationAccess Access, string Instructions, IReadOnlyList<string> Scope,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> AcceptanceCriteria);
public sealed record PlanProposal(int SchemaVersion, string Title, string Objective, IReadOnlyList<ProposedTask> Tasks);
public enum ProposalReviewState { Draft, Approved }
public sealed record WorkspaceProposal(string Id, string ProjectId, string SourceRunId, PlanProposal Definition,
    ProposalReviewState State, int Revision);
public sealed class PlanValidationException(string message) : ArgumentException(message);

/// <summary>Model output is a bounded proposal, never an executable control message.</summary>
public static class PlanProposalFormat
{
    public const string ChiefFunctionName = "Chefe do projeto";
    public const int MaxTasks = 20;
    private const string Fence = "```sintonia-plan\n";
    private const int JsonLimit = 128_000;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    public const string ChiefInstructions = """
        Atue como chefe do projeto. Apenas examine e planeje; não altere arquivos, não execute o plano,
        não delegue a outros agentes nem aprove entregas ou permissões pelo usuário.
        Ao receber um objetivo, proponha entre 1 e 20 tarefas pequenas para revisão no Sintonia.
        Responda com uma explicação breve e exatamente um bloco de código marcado sintonia-plan.
        Dentro dele, use JSON válido no formato abaixo, sem campos adicionais:
        {"schemaVersion":1,"title":"Título do plano","objective":"Objetivo verificável",
         "tasks":[{"id":"task-1","title":"Título da tarefa","functionName":"Desenvolvimento",
           "provider":"Codex","model":null,"access":"WorkspaceWrite",
           "instructions":"Instruções da entrega","scope":["src/"],"dependencies":[],
           "acceptanceCriteria":["Critério verificável"]}]}
        Provedor: Codex ou Claude. Modelo: null para o padrão da instalação, ou identificador conhecido;
        não invente disponibilidade. Acesso: ReadOnly ou WorkspaceWrite, como recomendação para revisão.
        Cada tarefa deve ter ID único de até 32 caracteres (letras ASCII, números, hífen ou sublinhado),
        escopo com caminhos relativos ao projeto e critérios de entrega. Use '.' para todo o projeto
        somente quando necessário. Não use caminhos absolutos, '..', curingas ou comandos como caminhos.
        Dependências são IDs de tarefas deste plano, sem repetição, autorreferência ou ciclos.
        Limites por tarefa: título 120, função 80, modelo 120 e instruções 8.000 caracteres;
        até 12 caminhos (240 caracteres cada) e 8 critérios (1.000 caracteres cada).
        O plano não concede permissões nem inicia tarefas. O usuário poderá editar e confirmar a proposta.
        """;

    public static bool TryParseResponse(string? response, out PlanProposal? proposal, out string error)
    {
        proposal = null;
        try
        {
            if (string.IsNullOrWhiteSpace(response) || response.Length > 256_000)
                throw new PlanValidationException("Resposta vazia ou acima do limite para uma proposta.");
            var text = response.Replace("\r\n", "\n", StringComparison.Ordinal);
            var start = text.IndexOf(Fence, StringComparison.Ordinal);
            if (start < 0 || (start > 0 && text[start - 1] != '\n')
                || text.IndexOf(Fence, start + Fence.Length, StringComparison.Ordinal) >= 0)
                throw new PlanValidationException("A resposta precisa conter exatamente uma proposta estruturada.");
            var bodyStart = start + Fence.Length;
            var end = text.IndexOf("\n```", bodyStart, StringComparison.Ordinal);
            if (end < 0 || (end + 4 < text.Length && text[end + 4] != '\n'))
                throw new PlanValidationException("O bloco da proposta está incompleto.");
            proposal = ParseJson(text[bodyStart..end]);
            error = "";
            return true;
        }
        catch (PlanValidationException exception) { error = exception.Message; return false; }
    }

    public static PlanProposal ParseJson(string json)
    {
        if (json.Length > JsonLimit) throw new PlanValidationException("A proposta excedeu o limite local de tamanho.");
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 16 });
            CheckDuplicateFields(document.RootElement);
            var proposal = JsonSerializer.Deserialize<PlanProposal>(json, Options)
                ?? throw new PlanValidationException("Proposta ausente.");
            Validate(proposal);
            return proposal;
        }
        catch (JsonException) { throw new PlanValidationException("A proposta contém JSON inválido, campos desconhecidos ou valores incompatíveis."); }
    }

    public static string Serialize(PlanProposal proposal)
    {
        Validate(proposal);
        var json = JsonSerializer.Serialize(proposal, Options);
        if (json.Length > JsonLimit) throw new PlanValidationException("A proposta excedeu o limite local de tamanho.");
        return json;
    }

    public static void Validate(PlanProposal proposal)
    {
        if (proposal.SchemaVersion != 1) throw new PlanValidationException("Versão de proposta não suportada.");
        Text(proposal.Title, 120, "Título do plano");
        Text(proposal.Objective, 8000, "Objetivo");
        if (proposal.Tasks is null || proposal.Tasks.Count is < 1 or > MaxTasks)
            throw new PlanValidationException($"O plano deve conter entre 1 e {MaxTasks} tarefas.");
        foreach (var task in proposal.Tasks)
        {
            if (task is null) throw new PlanValidationException("Tarefa ausente.");
            Text(task.Id, 32, "Identificador");
            if (task.Id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
                throw new PlanValidationException("Use apenas letras ASCII, números, hífen ou sublinhado nos identificadores.");
            Text(task.Title, 120, $"Título de {task.Id}");
            Text(task.FunctionName, 80, $"Função de {task.Id}");
            Text(task.Instructions, 8000, $"Instruções de {task.Id}");
            if (task.Model is not null) Text(task.Model, 120, $"Modelo de {task.Id}");
            if (!Enum.IsDefined(task.Provider) || !Enum.IsDefined(task.Access))
                throw new PlanValidationException($"Provedor ou acesso inválido em {task.Id}.");
            Items(task.Scope, 12, 240, $"Escopo de {task.Id}");
            foreach (var path in task.Scope) ValidateRelativeScope(path);
            Items(task.AcceptanceCriteria, 8, 1000, $"Critérios de {task.Id}");
            if (task.Dependencies is null || task.Dependencies.Count > MaxTasks
                || task.Dependencies.Any(d => string.IsNullOrWhiteSpace(d) || d.Length > 32)
                || task.Dependencies.Distinct(StringComparer.Ordinal).Count() != task.Dependencies.Count
                || task.Dependencies.Contains(task.Id, StringComparer.Ordinal))
                throw new PlanValidationException($"Dependências inválidas em {task.Id}.");
        }
        try
        {
            SchedulingPolicy.ValidateGraph(proposal.Tasks.Select(t => new WorkTask(t.Id, t.Title, t.Instructions,
                t.FunctionName, t.Dependencies)).ToArray());
        }
        catch (ArgumentException exception) { throw new PlanValidationException(exception.Message.Split(" (Parameter", StringSplitOptions.None)[0]); }
    }

    private static void ValidateRelativeScope(string path)
    {
        if (path == ".") return;
        var normalized = path.Replace('\\', '/');
        var segments = normalized.TrimEnd('/').Split('/');
        if (normalized.StartsWith('/') || normalized.Any(c => char.IsControl(c) || ":<>\"|?*".Contains(c))
            || segments.Any(s => s.Length == 0 || s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || IsDeviceName(s)))
            throw new PlanValidationException("O escopo deve conter caminhos relativos válidos, sem '..' ou curingas.");
    }
    private static bool IsDeviceName(string segment)
    {
        var name = segment.Split('.')[0].ToUpperInvariant();
        return name is "CON" or "PRN" or "AUX" or "NUL" || name.Length == 4
            && (name.StartsWith("COM", StringComparison.Ordinal) || name.StartsWith("LPT", StringComparison.Ordinal)) && name[3] is >= '1' and <= '9';
    }
    private static void Text(string? value, int limit, string label)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new PlanValidationException($"{label}: preencha um texto válido de até {limit} caracteres.");
    }
    private static void Items(IReadOnlyList<string>? values, int count, int length, string label)
    {
        if (values is null || values.Count < 1 || values.Count > count) throw new PlanValidationException($"{label}: informe entre 1 e {count} itens.");
        foreach (var value in values) Text(value, length, label);
    }
    private static void CheckDuplicateFields(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new PlanValidationException("A proposta contém campos repetidos.");
                CheckDuplicateFields(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) CheckDuplicateFields(item);
    }
}
