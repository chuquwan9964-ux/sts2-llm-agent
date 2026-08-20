using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.MonsterMoves.Intents;
using MegaCrit.Sts2.Core.Nodes;
using MegaCrit.Sts2.Core.Nodes.Cards.Holders;
using MegaCrit.Sts2.Core.Nodes.CommonUi;
using MegaCrit.Sts2.Core.Nodes.Events;
using MegaCrit.Sts2.Core.Nodes.GodotExtensions;
using MegaCrit.Sts2.Core.Nodes.Rewards;
using MegaCrit.Sts2.Core.Nodes.RestSite;
using MegaCrit.Sts2.Core.Nodes.Rooms;
using MegaCrit.Sts2.Core.Nodes.Screens;
using MegaCrit.Sts2.Core.Nodes.Screens.CardSelection;
using MegaCrit.Sts2.Core.Nodes.Screens.Map;
using MegaCrit.Sts2.Core.Nodes.Screens.Overlays;
using MegaCrit.Sts2.Core.Nodes.Screens.Shops;
using MegaCrit.Sts2.Core.Nodes.Screens.TreasureRoomRelic;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Models;
using Sts2LlmAgent.Core;

namespace Sts2LlmAgent;

/// <summary>Runs one validated decision at a time on the Godot main thread.</summary>
public sealed class LlmAgentController : Node
{
    private readonly AgentConfig _config;
    private readonly ChatClient _client;
    private CancellationTokenSource _lifetime = new();
    private bool _busy;
    private bool _started;
    private bool _reportedMultiplayer;
    private string? _lastFingerprint;
    private ulong _nextRequestAt;

    private sealed record CombatBinding(AgentAction Action, CardModel? Card, PotionModel? Potion, Creature? Target);

    public LlmAgentController(AgentConfig config)
    {
        _config = config;
        _client = new ChatClient(config);
        Name = "Sts2LlmAgentController";
        ProcessMode = ProcessModeEnum.Always;
    }

    public override void _Ready() => GD.Print("[Sts2LlmAgent] controller ready");

    public void Start()
    {
        if (_started) return;
        _started = true;
        GD.Print("[Sts2LlmAgent] controller loop started");
        _ = RunLoopAsync();
    }

    public override void _ExitTree() { _lifetime.Cancel(); _client.Dispose(); }

    private async Task RunLoopAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested && GodotObject.IsInstanceValid(this) && IsInsideTree())
            {
                await FrameAsync();
                if (!_busy) await TickAsync();
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            GD.PrintErr($"[Sts2LlmAgent] loop stopped: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task TickAsync()
    {
        _busy = true;
        try
        {
            RunState? run = RunManager.Instance?.DebugOnlyGetState();
            if (run is null) { await FrameAsync(); return; }
            if (run.Players.Count != 1)
            {
                if (!_reportedMultiplayer) { GD.PrintErr("[Sts2LlmAgent] Multiplayer run refused; the agent supports exactly one player."); _reportedMultiplayer = true; }
                await FrameAsync();
                return;
            }
            _reportedMultiplayer = false;
            Decision? decision = BuildDecision(run);
            if (decision is null || decision.Actions.Count == 0) { await FrameAsync(); return; }
            string fingerprint = DecisionProtocol.BuildUserJson(decision);
            ulong now = Time.GetTicksMsec();
            if (fingerprint == _lastFingerprint && now < _nextRequestAt) { await FrameAsync(); return; }
            _lastFingerprint = fingerprint;
            _nextRequestAt = now + 1500;
            string? response = await AskWithRetryAsync(decision);
            await FrameAsync();
            if (!NGame.IsMainThread()) throw new InvalidOperationException("LLM continuation left the Godot main thread.");
            await ResolveDecisionAsync(decision, response);
        }
        catch (Exception exception) { GD.PrintErr($"[Sts2LlmAgent] {exception.GetType().Name}: {exception.Message}"); await FrameAsync(); }
        finally { _busy = false; }
    }

    private async Task<string?> AskWithRetryAsync(Decision decision)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                string? response = await _client.ChooseAsync(decision, _lifetime.Token);
                if (DecisionProtocol.TryParseAndValidate(response, decision.Actions, out _)) return response;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            { if (_config.Verbose) GD.PrintErr($"[Sts2LlmAgent] request failed: {exception.GetType().Name}"); }
        }
        return null;
    }

    private Decision? BuildDecision(RunState run)
    {
        if (NOverlayStack.Instance?.Peek() is Node overlay) return OverlayDecision(overlay);
        if (CombatManager.Instance.IsInProgress) return CombatDecision(run);
        NGame? game = NGame.Instance;
        if (game?.CurrentRunNode is null) return null;
        return RoomDecision(game.CurrentRunNode, run) ?? MapDecision(game.CurrentRunNode, run);
    }

    private Decision? CombatDecision(RunState run)
    {
        Player? player = LocalContext.GetMe(run);
        PlayerCombatState? pcs = player?.PlayerCombatState;
        ICombatState? combat = player?.Creature.CombatState;
        if (player is null || pcs?.Phase != PlayerTurnPhase.Play || combat is null) return null;
        List<CombatBinding> bindings = BuildCombatBindings(player);
        bindings.Add(new(new("end", "end_turn", "End turn"), null, null, null));
        var hand = pcs.Hand.Cards.Select((card, index) => new
        {
            key = $"h{index}",
            model = card.Id.Entry,
            title = card.Title,
            type = card.Type.ToString(),
            cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(),
            upgraded = card.IsUpgraded,
            description = SafeText(() => card.GetDescriptionForPile(PileType.Hand)),
            playable = card.CanPlay(out _, out _)
        }).ToList();
        var potions = player.PotionSlots.Select((potion, index) => potion is null ? null : new
        {
            key = $"s{index}",
            model = potion.Id.Entry,
            title = SafeText(() => potion.Title.GetFormattedText()),
            description = SafeText(() => potion.DynamicDescription.GetFormattedText()),
            target = potion.TargetType.ToString(),
            usable = CanUsePotion(player, potion)
        }).Where(potion => potion is not null).ToList();
        var observation = new
        {
            turn = pcs.TurnNumber,
            round = combat.RoundNumber,
            phase = pcs.Phase.ToString(),
            player = new
            {
                hp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                block = player.Creature.Block,
                energy = pcs.Energy,
                maxEnergy = pcs.MaxEnergy,
                stars = pcs.Stars,
                powers = PowerObservation(player.Creature)
            },
            piles = new { draw = pcs.DrawPile.Cards.Count, discard = pcs.DiscardPile.Cards.Count, exhaust = pcs.ExhaustPile.Cards.Count },
            hand,
            potions,
            enemies = combat.Enemies.Select((enemy, index) => new
            {
                key = $"e{index}",
                model = enemy.ModelId.Entry,
                name = enemy.Name,
                hp = enemy.CurrentHp,
                maxHp = enemy.MaxHp,
                block = enemy.Block,
                powers = PowerObservation(enemy),
                intents = enemy.Monster?.NextMove.Intents.Select(intent => IntentObservation(intent, combat, enemy)).ToList()
            }).ToList()
        };
        return new("combat", observation, bindings.Select(binding => binding.Action).ToList());
    }

    private static List<CombatBinding> BuildCombatBindings(Player player)
    {
        var bindings = new List<CombatBinding>();
        PlayerCombatState? pcs = player.PlayerCombatState;
        ICombatState? combat = player.Creature.CombatState;
        if (pcs?.Phase != PlayerTurnPhase.Play || combat is null) return bindings;
        for (int handIndex = 0; handIndex < pcs.Hand.Cards.Count; handIndex++)
        {
            CardModel card = pcs.Hand.Cards[handIndex];
            if (!card.CanPlay(out _, out _)) continue;
            if (card.IsValidTarget(null))
            {
                var action = new AgentAction($"card:h{handIndex}:none", "play_card", $"Play h{handIndex} {card.Title}");
                bindings.Add(new(action, card, null, null));
            }
            for (int targetIndex = 0; targetIndex < combat.Creatures.Count; targetIndex++)
            {
                Creature target = combat.Creatures[targetIndex];
                if (!target.IsHittable || !card.IsValidTarget(target)) continue;
                var action = new AgentAction($"card:h{handIndex}:t{targetIndex}", "play_card", $"Play h{handIndex} {card.Title} on {target.Name}");
                bindings.Add(new(action, card, null, target));
            }
        }
        for (int slotIndex = 0; slotIndex < player.PotionSlots.Count; slotIndex++)
        {
            PotionModel? potion = player.PotionSlots[slotIndex];
            if (potion is null || !CanUsePotion(player, potion)) continue;
            string title = SafeText(() => potion.Title.GetFormattedText()) ?? potion.Id.Entry;
            if (potion.IsValidTarget(null))
            {
                var action = new AgentAction($"potion:s{slotIndex}:none", "use_potion", $"Use s{slotIndex} {title}");
                bindings.Add(new(action, null, potion, null));
            }
            for (int targetIndex = 0; targetIndex < combat.Creatures.Count; targetIndex++)
            {
                Creature target = combat.Creatures[targetIndex];
                if (!potion.IsValidTarget(target)) continue;
                var action = new AgentAction($"potion:s{slotIndex}:t{targetIndex}", "use_potion", $"Use s{slotIndex} {title} on {target.Name}");
                bindings.Add(new(action, null, potion, target));
            }
        }
        return bindings;
    }

    private static bool CanUsePotion(Player player, PotionModel potion) =>
        !potion.IsQueued && !potion.HasBeenRemovedFromState && player.Creature.IsAlive && player.CanRemovePotions && potion.PassesCustomUsabilityCheck && potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime;

    private static object PowerObservation(Creature creature) => creature.Powers.Where(power => power.IsVisible).Select(power => new
    {
        id = power.Id.Entry,
        title = SafeText(() => power.Title.GetFormattedText()),
        description = SafeText(() => power.SmartDescription.GetFormattedText()),
        amount = power.DisplayAmount,
        type = power.TypeForCurrentAmount.ToString()
    }).ToList();

    private static object IntentObservation(AbstractIntent intent, ICombatState combat, Creature owner)
    {
        AttackIntent? attack = intent as AttackIntent;
        return new
        {
            type = intent.IntentType.ToString(),
            label = SafeText(() => intent.GetIntentLabel(combat.Allies, owner).GetFormattedText()),
            damage = attack is null ? null : SafeInt(() => attack.GetSingleDamage(combat.Allies, owner)),
            hits = attack?.Repeats,
            total = attack is null ? null : SafeInt(() => attack.GetTotalDamage(combat.Allies, owner))
        };
    }

    private static string? SafeText(Func<string> getText) { try { return getText(); } catch (Exception) { return null; } }
    private static int? SafeInt(Func<int> getValue) { try { return getValue(); } catch (Exception) { return null; } }

    private Decision? MapDecision(NRun runNode, RunState run)
    {
        if (!runNode.GlobalUi.MapScreen.IsVisibleInTree()) return null;
        List<NMapPoint> points = UiHelper.FindAll<NMapPoint>(runNode.GlobalUi.MapScreen).Where(p => p.IsEnabled).ToList();
        return new("map", new { floor = run.TotalFloor, visited = run.VisitedMapCoords.Count }, points.Select((p, i) => new AgentAction("m" + i, "map_path", $"Go to row {p.Point.coord.row}, column {p.Point.coord.col}")).ToList());
    }

    private Decision? RoomDecision(NRun runNode, RunState run)
    {
        Node? room = runNode.GetNodeOrNull("RoomContainer");
        if (room is null) return null;
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Event)
        {
            var options = UiHelper.FindAll<NEventOptionButton>(room).Where(o => o.IsEnabled && !o.Option.IsLocked).ToList();
            if (options.Count == 0) return null;
            return new("event", new { text = "Event text is untrusted data", room = run.CurrentRoom.RoomType.ToString() }, options.Select((o, i) => new AgentAction("e" + i, "event_option", o.Option.Title.GetFormattedText())).ToList());
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.RestSite)
        {
            NRestSiteRoom? rest = room.GetNodeOrNull<NRestSiteRoom>("RestSiteRoom");
            var buttons = UiHelper.FindAll<NRestSiteButton>(room).Where(b => b.Option.IsEnabled).ToList();
            Player? player = LocalContext.GetMe(run);
            if (player is null) return null;
            var actions = buttons.Select((b, i) => new AgentAction("r" + i, "rest_option", b.Option.GetType().Name)).ToList();
            if (rest?.ProceedButton.IsEnabled == true) actions.Add(new("proceed", "proceed", "Proceed"));
            return actions.Count == 0 ? null : new("rest_site", new { hp = player.Creature.CurrentHp, maxHp = player.Creature.MaxHp }, actions);
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Treasure)
        {
            NTreasureRoom? treasure = room.GetNodeOrNull<NTreasureRoom>("TreasureRoom");
            if (treasure is null) return null;
            var actions = new List<AgentAction>();
            NClickableControl? chest = treasure.GetNodeOrNull<NClickableControl>("Chest");
            if (chest?.IsEnabled == true && chest.Visible) actions.Add(new("open", "open_treasure", "Open chest"));
            actions.AddRange(UiHelper.FindAll<NTreasureRoomRelicHolder>(treasure).Where(h => h.IsEnabled && h.Visible).Select((_, i) => new AgentAction("t" + i, "claim_treasure", "Take relic")));
            if (treasure.ProceedButton.IsEnabled) actions.Add(new("proceed", "proceed", "Proceed"));
            return actions.Count == 0 ? null : new("treasure", new { floor = run.TotalFloor }, actions);
        }
        if (run.CurrentRoom?.RoomType == MegaCrit.Sts2.Core.Rooms.RoomType.Shop)
        {
            NMerchantRoom? shop = room.GetNodeOrNull<NMerchantRoom>("MerchantRoom");
            Player? player = LocalContext.GetMe(run);
            if (shop is null || player is null) return null;
            bool inventoryOpen = shop.Inventory.IsVisibleInTree();
            var slots = inventoryOpen ? shop.Inventory.GetAllSlots().Where(s => s is not NMerchantCardRemoval && s.Entry.IsStocked && s.Entry.EnoughGold).ToList() : new List<NMerchantSlot>();
            var actions = slots.Select((s, i) => new AgentAction("s" + i, "buy", $"Buy {s.Entry.GetType().Name} for {s.Entry.Cost} gold")).ToList();
            if (inventoryOpen) actions.Add(new("close", "close_shop", "Close merchant inventory"));
            else actions.Add(new("open", "open_shop", "Open merchant inventory"));
            if (!inventoryOpen && shop.ProceedButton.IsEnabled) actions.Add(new("proceed", "proceed", "Leave shop"));
            return new("shop", new { gold = player.Gold }, actions);
        }
        return null;
    }

    private Decision? OverlayDecision(Node overlay)
    {
        var cards = UiHelper.FindAll<NCardHolder>(overlay);
        if (cards.Count > 0)
        {
            List<AgentAction> actions = cards.Select((c, i) => new AgentAction("o" + i, "choose_card", c.CardModel?.Title ?? "card")).ToList();
            if (UiHelper.FindAll<NConfirmButton>(overlay).Any(b => b.IsEnabled)) actions.Add(new("confirm", "confirm", "Confirm selection"));
            if (UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).Any(b => b.IsEnabled)) actions.Add(new("skip", "skip", "Skip"));
            return new(overlay.GetType().Name, new { screen = overlay.GetType().Name }, actions);
        }
        var rewards = UiHelper.FindAll<NRewardButton>(overlay).Where(b => b.IsEnabled).ToList();
        if (rewards.Count > 0) return new("rewards", new { screen = overlay.GetType().Name }, rewards.Select((b, i) => new AgentAction("o" + i, "claim_reward", b.Reward?.GetType().Name ?? "reward")).ToList());
        var proceed = UiHelper.FindAll<NProceedButton>(overlay).Where(b => b.IsEnabled).ToList();
        if (proceed.Count > 0) return new("proceed", new { screen = overlay.GetType().Name }, new[] { new AgentAction("proceed", "proceed", "Proceed") });
        var clickables = UiHelper.FindAll<NClickableControl>(overlay).Where(b => b.IsEnabled && b.Visible).ToList();
        return clickables.Count == 0 ? null : new("generic_overlay", new { screen = overlay.GetType().Name, gameText = "untrusted" }, clickables.Select((b, i) => new AgentAction("g" + i, "click", b.GetType().Name)).ToList());
    }

    private async Task ResolveDecisionAsync(Decision decision, string? response)
    {
        AgentAction? action;
        if (!DecisionProtocol.TryParseAndValidate(response, decision.Actions, out ModelDecision chosen))
        {
            action = DecisionPolicy.ConservativeFallback(decision);
            if (action is null)
            {
                if (_config.Verbose) GD.Print($"[Sts2LlmAgent] {decision.Screen}: no safe fallback; waiting");
                return;
            }
            chosen = new(action.Id, "fallback");
        }
        else action = decision.Actions.Single(candidate => candidate.Id == chosen.ActionId);
        if (_config.Verbose && chosen.Reason.Length > 0) GD.Print($"[Sts2LlmAgent] {decision.Screen}: {chosen.Reason}");
        await ExecuteAsync(decision.Screen, action).WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
    }

    private async Task ExecuteAsync(string screen, AgentAction action)
    {
        await FrameAsync();
        RunState? liveRun = RunManager.Instance?.DebugOnlyGetState();
        Decision? liveDecision = liveRun is null || liveRun.Players.Count != 1 ? null : BuildDecision(liveRun);
        AgentAction? liveAction = liveDecision?.Screen == screen ? liveDecision.Actions.SingleOrDefault(candidate => candidate.Id == action.Id && candidate.Kind == action.Kind) : null;
        if (liveAction is null) return;
        if (screen == "combat") { await ExecuteCombat(liveAction); return; }
        if (NOverlayStack.Instance?.Peek() is Node overlay) { ExecuteOverlay(overlay, liveAction); return; }
        NRun? run = NGame.Instance?.CurrentRunNode;
        if (run is null) return;
        action = liveAction;
        if (screen == "map") { var points = UiHelper.FindAll<NMapPoint>(run.GlobalUi.MapScreen).Where(p => p.IsEnabled).ToList(); int i = Index(action.Id); if (i >= 0 && i < points.Count) await UiHelper.Click(points[i]); return; }
        Node room = run.GetNode("RoomContainer"); int index = Index(action.Id);
        if (screen == "event") { var items = UiHelper.FindAll<NEventOptionButton>(room).Where(o => o.IsEnabled && !o.Option.IsLocked).ToList(); if (index >= 0 && index < items.Count) await UiHelper.Click(items[index]); }
        else if (screen == "rest_site") { var rest = room.GetNodeOrNull<NRestSiteRoom>("RestSiteRoom"); if (action.Kind == "proceed" && rest?.ProceedButton.IsEnabled == true) await UiHelper.Click(rest.ProceedButton); else { var items = UiHelper.FindAll<NRestSiteButton>(room).Where(b => b.Option.IsEnabled).ToList(); if (index >= 0 && index < items.Count) await UiHelper.Click(items[index]); } }
        else if (screen == "treasure") { var treasure = room.GetNodeOrNull<NTreasureRoom>("TreasureRoom"); if (treasure is null) return; if (action.Id == "open") { var chest = treasure.GetNodeOrNull<NClickableControl>("Chest"); if (chest?.IsEnabled == true) await UiHelper.Click(chest); } else if (action.Kind == "claim_treasure") { var holders = UiHelper.FindAll<NTreasureRoomRelicHolder>(treasure).Where(h => h.IsEnabled && h.Visible).ToList(); if (index >= 0 && index < holders.Count) await UiHelper.Click(holders[index]); } else if (treasure.ProceedButton.IsEnabled) await UiHelper.Click(treasure.ProceedButton); }
        else if (screen == "shop") { var shop = room.GetNodeOrNull<NMerchantRoom>("MerchantRoom"); if (shop is null) return; if (action.Kind == "buy") { var slots = shop.Inventory.GetAllSlots().Where(s => s is not NMerchantCardRemoval && s.Entry.IsStocked && s.Entry.EnoughGold).ToList(); if (index >= 0 && index < slots.Count) await slots[index].Entry.OnTryPurchaseWrapper(shop.Inventory.Inventory); } else if (action.Kind == "open_shop" && !shop.Inventory.IsVisibleInTree()) shop.OpenInventory(); else if (action.Kind == "close_shop") UiHelper.FindAll<NBackButton>(shop).FirstOrDefault(b => b.IsEnabled)?.ForceClick(); else if (shop.ProceedButton.IsEnabled) await UiHelper.Click(shop.ProceedButton); }
    }

    private async Task ExecuteCombat(AgentAction action)
    {
        RunState? run = RunManager.Instance?.DebugOnlyGetState(); if (run is null || run.Players.Count != 1) return;
        Player? player = LocalContext.GetMe(run); if (player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play) return;
        if (action.Kind == "end_turn") { PlayerCmd.EndTurn(player, false); return; }
        CombatBinding? binding = BuildCombatBindings(player).SingleOrDefault(candidate => candidate.Action.Id == action.Id && candidate.Action.Kind == action.Kind);
        if (binding?.Card is CardModel card && card.TryManualPlay(binding.Target))
        {
            ulong deadline = Time.GetTicksMsec() + 10_000;
            while (Time.GetTicksMsec() < deadline
                && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play
                && card.Pile?.Type == PileType.Hand)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                await FrameAsync();
            }
        }
        else if (binding?.Potion is PotionModel potion && CanUsePotion(player, potion) && potion.IsValidTarget(binding.Target)) potion.EnqueueManualUse(binding.Target);
    }

    private static void ExecuteOverlay(Node overlay, AgentAction action)
    {
        int i = Index(action.Id); if (i < 0) return;
        if (action.Kind == "skip") { UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).FirstOrDefault(b => b.IsEnabled)?.ForceClick(); }
        else if (action.Kind == "confirm") { UiHelper.FindAll<NConfirmButton>(overlay).FirstOrDefault(b => b.IsEnabled)?.ForceClick(); }
        else if (action.Kind == "choose_card") { var cards = UiHelper.FindAll<NCardHolder>(overlay); if (i < cards.Count) cards[i].EmitSignal(NCardHolder.SignalName.Pressed, cards[i]); }
        else if (action.Kind == "claim_reward") { var rewards = UiHelper.FindAll<NRewardButton>(overlay).Where(b => b.IsEnabled).ToList(); if (i < rewards.Count) rewards[i].ForceClick(); }
        else if (action.Kind == "click") { var buttons = UiHelper.FindAll<NClickableControl>(overlay).Where(b => b.IsEnabled && b.Visible).ToList(); if (i < buttons.Count) buttons[i].ForceClick(); }
        else { var button = UiHelper.FindAll<NProceedButton>(overlay).FirstOrDefault(b => b.IsEnabled); button?.ForceClick(); }
    }

    private static int Index(string id) => int.TryParse(id.AsSpan(1), out int value) ? value : -1;
    private async Task FrameAsync() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
}
