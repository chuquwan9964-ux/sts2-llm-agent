using Sts2LlmAgent.Core;

static void Check(bool value, string message) { if (!value) throw new Exception(message); }
var env = new Dictionary<string, string?> { ["STS2_LLM_AGENT_ENABLED"] = "true", ["STS2_LLM_BASE_URL"] = "https://example.test/", ["STS2_LLM_TIMEOUT_SECONDS"] = "12" };
AgentConfig config = AgentConfig.FromEnvironment(env);
Check(config.Enabled && config.BaseUrl == "https://example.test" && config.Timeout == TimeSpan.FromSeconds(12), "config");
Check(config.ChatCompletionsUrl == "https://example.test/v1/chat/completions", "endpoint root");
Check(EndpointNormalizer.ChatCompletions("https://example.test/v1/") == "https://example.test/v1/chat/completions", "endpoint v1");
Check(EndpointNormalizer.ChatCompletions("https://example.test/v1/chat/completions") == "https://example.test/v1/chat/completions", "endpoint complete");
var actions = new[] { new AgentAction("a0", "skip", "Skip") };
Check(DecisionProtocol.TryParseAndValidate("{\"action_id\":\"a0\",\"reason\":\"ok\"}", actions, out _), "valid response");
Check(!DecisionProtocol.TryParseAndValidate("{\"action_id\":\"hack\",\"reason\":\"no\"}", actions, out _), "invalid id");
Check(!DecisionProtocol.TryParseAndValidate("{\"action_id\":\"a0\"}", actions, out _), "missing reason");
Check(!DecisionProtocol.TryParseAndValidate("{\"action_id\":\"a0\",\"reason\":\"ok\",\"extra\":1}", actions, out _), "extra field");
Check(!DecisionProtocol.TryParseAndValidate("{\"action_id\":\"a0\",\"action_id\":\"a0\",\"reason\":\"ok\"}", actions, out _), "duplicate json field");
Check(!DecisionProtocol.TryParseAndValidate("{\"action_id\":\"a0\",\"reason\":\"ok\"}", [actions[0], actions[0]], out _), "duplicate legal id");
Check(!DecisionProtocol.TryParseAndValidate("not json", actions, out _), "invalid json");
Check(AgentConfig.FromEnvironment(new Dictionary<string, string?> { ["STS2_LLM_TIMEOUT_SECONDS"] = "bad" }).Timeout == TimeSpan.FromSeconds(45), "invalid timeout default");
Check(AgentConfig.FromEnvironment(new Dictionary<string, string?> { ["STS2_LLM_AGENT_ENABLED"] = "YES", ["STS2_LLM_VERBOSE"] = "1" }) is { Enabled: true, Verbose: true }, "true bool config");
Check(AgentConfig.FromEnvironment(new Dictionary<string, string?> { ["STS2_LLM_AGENT_ENABLED"] = "false", ["STS2_LLM_VERBOSE"] = "no" }) is { Enabled: false, Verbose: false }, "false bool config");
Check(DecisionPolicy.ConservativeFallback(new Decision("combat", new { }, [new("play", "play_card", "Play"), new("end", "end_turn", "End")]))?.Id == "end", "combat fallback");
Check(DecisionPolicy.ConservativeFallback(new Decision("generic_overlay", new { }, [new("g0", "click", "Unknown")])) is null, "generic fallback pauses");
Check(DecisionPolicy.ConservativeFallback(new Decision("shop", new { }, [new("s0", "buy", "Buy"), new("close", "close_shop", "Close")]))?.Id == "close", "shop fallback");
Console.WriteLine("Sts2LlmAgent.Core tests passed");
