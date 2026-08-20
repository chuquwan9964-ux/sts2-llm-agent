using Godot;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Nodes;
using Sts2LlmAgent.Core;

namespace Sts2LlmAgent;

[ModInitializer(nameof(Initialize))]
public static class LlmAgentInitializer
{
    private static bool _attached;

    public static void Initialize()
    {
        AgentConfig config = AgentConfig.FromEnvironment();
        GD.Print($"[Sts2LlmAgent] initializer enabled={config.Enabled}, apiKeyPresent={!string.IsNullOrWhiteSpace(config.ApiKey)}, model={config.Model}");
        if (!config.Enabled || string.IsNullOrWhiteSpace(config.ApiKey)) return;
        AttachOrRetry(config);
    }

    private static void AttachOrRetry(AgentConfig config)
    {
        if (_attached) return;
        NGame? game = NGame.Instance;
        if (game is null || !game.IsInsideTree() || !game.IsNodeReady())
        {
            if (Engine.GetMainLoop() is SceneTree) Callable.From(() => AttachOrRetry(config)).CallDeferred();
            return;
        }
        if (!NGame.IsMainThread()) { Callable.From(() => AttachOrRetry(config)).CallDeferred(); return; }
        _attached = true;
        LlmAgentController controller = new(config);
        Callable.From(() =>
        {
            if (!GodotObject.IsInstanceValid(game) || !game.IsInsideTree() || game.GetTree() is null)
            {
                _attached = false;
                AttachOrRetry(config);
                return;
            }
            Node root = game.GetTree().Root;
            root.AddChild(controller);
            GD.Print($"[Sts2LlmAgent] controller attached insideTree={controller.IsInsideTree()} ready={controller.IsNodeReady()}");
            controller.Start();
        }).CallDeferred();
    }
}
