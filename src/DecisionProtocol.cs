using System.Text.Json;

namespace Sts2LlmAgent.Core;

public sealed record AgentAction(string Id, string Kind, string Label);
public sealed record AgentMemory(string ActGoal, string TurnPlan, string CombatPlan, string DeckPlan, string RoutePlan, string PotionPlan);
public sealed record RecentAction(string Screen, string ActionId, string Kind, string Label, string Result);
public sealed record Decision(string Screen, object Observation, IReadOnlyList<AgentAction> Actions, AgentMemory? Memory = null, IReadOnlyList<RecentAction>? RecentActions = null);
public sealed record ModelDecision(string ActionId, string Reason, AgentMemory? Memory = null);

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
            int propertyCount = root.EnumerateObject().Count();
            if (root.ValueKind != JsonValueKind.Object || propertyCount is < 2 or > 3 || !root.TryGetProperty("action_id", out JsonElement id) || id.ValueKind != JsonValueKind.String || !root.TryGetProperty("reason", out JsonElement reasonElement) || reasonElement.ValueKind != JsonValueKind.String) return false;
            if (root.EnumerateObject().Select(property => property.Name).Distinct().Count() != propertyCount) return false;
            if (root.EnumerateObject().Any(property => property.Name is not ("action_id" or "reason" or "memory"))) return false;
            string actionId = id.GetString() ?? string.Empty;
            if (actions.Count(action => action.Id == actionId) != 1) return false;
            string reason = reasonElement.GetString() ?? string.Empty;
            AgentMemory? memory = null;
            if (root.TryGetProperty("memory", out JsonElement memoryElement))
            {
                if (memoryElement.ValueKind != JsonValueKind.Object) return false;
                string[] fields = ["actGoal", "turnPlan", "combatPlan", "deckPlan", "routePlan", "potionPlan"];
                if (memoryElement.EnumerateObject().Count() != fields.Length || memoryElement.EnumerateObject().Select(property => property.Name).Distinct().Count() != fields.Length) return false;
                string? Get(string name) => memoryElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                string? actGoal = Get("actGoal");
                string? turnPlan = Get("turnPlan");
                string? combatPlan = Get("combatPlan");
                string? deckPlan = Get("deckPlan");
                string? routePlan = Get("routePlan");
                string? potionPlan = Get("potionPlan");
                if (actGoal is null || turnPlan is null || combatPlan is null || deckPlan is null || routePlan is null || potionPlan is null) return false;
                memory = new(actGoal, turnPlan, combatPlan, deckPlan, routePlan, potionPlan);
            }
            decision = new ModelDecision(actionId, reason, memory);
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
