namespace Sts2LlmAgent.Core;

public sealed record AgentConfig(
    bool Enabled,
    string ApiKey,
    string BaseUrl,
    string Model,
    TimeSpan Timeout,
    bool Verbose)
{
    public string ChatCompletionsUrl => EndpointNormalizer.ChatCompletions(BaseUrl);

    public static AgentConfig FromEnvironment(IReadOnlyDictionary<string, string?>? environment = null)
    {
        string? Get(string name) => environment is null ? Environment.GetEnvironmentVariable(name) : environment.GetValueOrDefault(name);
        static bool Bool(string? value, bool fallback) => value is null ? fallback : value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase);
        static TimeSpan Duration(string? value) => int.TryParse(value, out int seconds) && seconds is > 0 and <= 600 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(45);
        string baseUrl = Get("STS2_LLM_BASE_URL") ?? string.Empty;
        string model = Get("STS2_LLM_MODEL") ?? string.Empty;
        return new(
            Bool(Get("STS2_LLM_AGENT_ENABLED"), false),
            Get("STS2_LLM_API_KEY") ?? string.Empty,
            (string.IsNullOrWhiteSpace(baseUrl) ? "https://api.deepseek.com" : baseUrl).TrimEnd('/'),
            string.IsNullOrWhiteSpace(model) ? "deepseek-chat" : model,
            Duration(Get("STS2_LLM_TIMEOUT_SECONDS")),
            Bool(Get("STS2_LLM_VERBOSE"), false));
    }
}

public static class EndpointNormalizer
{
    public static string ChatCompletions(string baseUrl)
    {
        string value = baseUrl.Trim().TrimEnd('/');
        if (value.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) return value + "/chat/completions";
        return value + "/v1/chat/completions";
    }
}
