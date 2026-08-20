using System.Text.Json;

namespace Sts2LlmAgent.Core;

public sealed record AgentAction(string Id, string Kind, string Label);
public sealed record Decision(string Screen, object Observation, IReadOnlyList<AgentAction> Actions);
public sealed record ModelDecision(string ActionId, string Reason);

public static class DecisionProtocol
{
    public static string BuildUserJson(Decision decision) => JsonSerializer.Serialize(decision, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    public static bool TryParseAndValidate(string? response, IReadOnlyList<AgentAction> actions, out ModelDecision decision)
    {
        decision = new ModelDecision(string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(response)) return false;
        try
        {
            using JsonDocument document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("action_id", out JsonElement id) || id.ValueKind != JsonValueKind.String || !root.TryGetProperty("reason", out JsonElement reasonElement) || reasonElement.ValueKind != JsonValueKind.String) return false;
            string actionId = id.GetString() ?? string.Empty;
            if (actions.Count(action => action.Id == actionId) != 1) return false;
            string reason = reasonElement.GetString() ?? string.Empty;
            decision = new ModelDecision(actionId, reason);
            return true;
        }
        catch (JsonException) { return false; }
    }
}

public static class DecisionPolicy
{
    private static readonly string[] SafeKinds = ["end_turn", "skip", "proceed", "close_shop", "map_path", "rest_option", "event_option", "choose_card", "claim_treasure", "open_treasure"];

    public static AgentAction? ConservativeFallback(Decision decision)
    {
        foreach (string kind in SafeKinds)
        {
            AgentAction? action = decision.Actions.FirstOrDefault(candidate => candidate.Kind == kind);
            if (action is not null) return action;
        }
        return null;
    }
}
