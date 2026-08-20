using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Godot;
using MegaCrit.Sts2.Core.AutoSlay.Helpers;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.CardRewardAlternatives;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Merchant;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Potions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Localization;
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
using MegaCrit.Sts2.Core.TestSupport;
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
    private string? _suppressedFingerprint;
    private ulong _suppressedUntil;
    private readonly List<RecentAction> _recentActions = [];
    private AgentMemory _memory = DefaultMemory(1);
    private RunState? _memoryRun;
    private int _memoryAct = -1;
    private bool _memoryWasCombat;
    private int _memoryTurn = -1;

    private sealed record CombatBinding(AgentAction Action, CardModel? Card, PotionModel? Potion, Creature? Target);
    private sealed record MerchantBinding(AgentAction Action, NMerchantSlot Slot);
    private sealed record RewardBinding(AgentAction Action, NRewardButton Button);

    private sealed class LlmCardSelector(LlmAgentController owner) : MegaCrit.Sts2.Core.TestSupport.ICardSelector
    {
        public async Task<IEnumerable<CardModel>> GetSelectedCards(IEnumerable<CardModel> options, int minSelect, int maxSelect)
        {
            List<CardModel> remaining = options.ToList();
            List<CardModel> selected = [];
            int maximum = Math.Min(maxSelect, remaining.Count);
            while (remaining.Count > 0 && selected.Count < maximum)
            {
                List<(CardModel Card, AgentAction Action)> bindings = StableCardActions(remaining, "nested");
                List<AgentAction> actions = bindings.Select(binding => binding.Action).ToList();
                if (selected.Count >= minSelect) actions.Add(new("done", "confirm", "Finish selection"));
                Decision decision = new("nested_card_choice", new
                {
                    minSelect,
                    maxSelect,
                    selected = selected.Select(card => CardObservation(card, "selected", card.Pile?.Type ?? PileType.None)).ToList(),
                    options = bindings.Select(binding => CardObservation(binding.Card, binding.Action.Id, binding.Card.Pile?.Type ?? PileType.None)).ToList()
                }, actions);
                string? response = await owner.AskWithRetryAsync(decision);
                if (!DecisionProtocol.TryParseAndValidate(response, actions, out ModelDecision choice))
                {
                    if (selected.Count >= minSelect) break;
                    choice = new(bindings[0].Action.Id, "fallback");
                }
                if (choice.ActionId == "done") break;
                (CardModel Card, AgentAction Action) binding = bindings.Single(binding => binding.Action.Id == choice.ActionId);
                if (owner._config.Verbose) GD.Print($"[Sts2LlmAgent] nested_card_choice: {choice.Reason}");
                selected.Add(binding.Card);
                remaining.Remove(binding.Card);
                owner.AddRecentAction(new("nested_card_choice", binding.Action.Id, binding.Action.Kind, binding.Action.Label, "selected"));
            }
            while (selected.Count < minSelect && remaining.Count > 0)
            {
                selected.Add(remaining[0]);
                remaining.RemoveAt(0);
            }
            return selected;
        }

        public CardRewardSelection GetSelectedCardReward(IReadOnlyList<CardCreationResult> options, IReadOnlyList<CardRewardAlternative> alternatives) => default;
    }

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
            UpdateMemoryLifecycle(run);
            Decision? decision = BuildDecision(run);
            if (decision is null || decision.Actions.Count == 0) { await FrameAsync(); return; }
            string fingerprint = DecisionProtocol.BuildUserJson(decision);
            ulong now = Time.GetTicksMsec();
            if (fingerprint != _suppressedFingerprint) _suppressedFingerprint = null;
            if (fingerprint == _suppressedFingerprint && now < _suppressedUntil) { await FrameAsync(); return; }
            if (fingerprint == _lastFingerprint && now < _nextRequestAt) { await FrameAsync(); return; }
            _lastFingerprint = fingerprint;
            _nextRequestAt = now + 1500;
            string? response = await AskWithRetryAsync(decision);
            await FrameAsync();
            if (!NGame.IsMainThread()) throw new InvalidOperationException("LLM continuation left the Godot main thread.");
            await ResolveDecisionAsync(decision, response);
            for (int i = 0; i < 15; i++) await FrameAsync();
            RunState? afterRun = RunManager.Instance?.DebugOnlyGetState();
            Decision? afterDecision = afterRun is null || afterRun.Players.Count != 1 ? null : BuildDecision(afterRun);
            if (afterDecision is not null && DecisionProtocol.BuildUserJson(afterDecision) == fingerprint)
            {
                _suppressedFingerprint = fingerprint;
                _suppressedUntil = Time.GetTicksMsec() + 30_000;
                GD.PrintErr($"[Sts2LlmAgent] {decision.Screen}: action caused no observable progress; suppressing this state for 30 seconds");
            }
        }
        catch (Exception exception) { GD.PrintErr($"[Sts2LlmAgent] {exception.GetType().Name}: {exception.Message}"); await FrameAsync(); }
        finally { _busy = false; }
    }

    private async Task<string?> AskWithRetryAsync(Decision decision)
    {
        Decision request = decision with { Memory = _memory, RecentActions = _recentActions.ToList() };
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                string? response = await _client.ChooseAsync(request, _lifetime.Token);
                if (DecisionProtocol.TryParseAndValidate(response, decision.Actions, out ModelDecision parsed))
                {
                    if (parsed.Memory is not null) _memory = parsed.Memory;
                    return response;
                }
                if (_config.Verbose)
                {
                    string summary = response ?? "<empty>";
                    if (summary.Length > 500) summary = summary[..500];
                    GD.PrintErr($"[Sts2LlmAgent] invalid LLM response for {decision.Screen}: {summary}");
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException)
            { GD.PrintErr($"[Sts2LlmAgent] request failed for {decision.Screen}: {exception.GetType().Name}: {exception.Message}"); }
        }
        return null;
    }

    private static AgentMemory DefaultMemory(int act) => new(
        $"Survive act {act}, reach its boss, and defeat it while preserving HP",
        string.Empty,
        string.Empty,
        "Build a compact deck with reliable damage, defense, and scaling",
        "Prefer routes that preserve enough HP and provide a rest site before the boss",
        "Save potions for meaningful HP preservation, dangerous fights, or the boss");

    private void UpdateMemoryLifecycle(RunState run)
    {
        if (!ReferenceEquals(_memoryRun, run))
        {
            _memoryRun = run;
            _memoryAct = run.CurrentActIndex;
            _memoryWasCombat = false;
            _memoryTurn = -1;
            _recentActions.Clear();
            _memory = DefaultMemory(run.CurrentActIndex + 1);
        }
        if (_memoryAct != run.CurrentActIndex)
        {
            _memoryAct = run.CurrentActIndex;
            _memory = _memory with
            {
                ActGoal = $"Survive act {run.CurrentActIndex + 1}, reach its boss, and defeat it while preserving HP",
                TurnPlan = string.Empty,
                CombatPlan = string.Empty,
                RoutePlan = "Re-evaluate the new act route and preserve a rest option before the boss"
            };
            _memoryTurn = -1;
        }
        bool inCombat = CombatManager.Instance.IsInProgress;
        if (inCombat && !_memoryWasCombat)
        {
            _memory = _memory with { TurnPlan = string.Empty, CombatPlan = "Assess enemy mechanics, intents, lethal timing, and a low-damage victory plan" };
            _memoryTurn = -1;
        }
        else if (!inCombat && _memoryWasCombat)
        {
            _memory = _memory with { TurnPlan = string.Empty, CombatPlan = string.Empty };
            _memoryTurn = -1;
        }
        _memoryWasCombat = inCombat;
        Player? player = LocalContext.GetMe(run);
        int turn = player?.PlayerCombatState?.TurnNumber ?? -1;
        if (inCombat && turn != _memoryTurn)
        {
            _memoryTurn = turn;
            _memory = _memory with { TurnPlan = string.Empty };
        }
    }

    private void AddRecentAction(RecentAction action)
    {
        _recentActions.Add(action);
        if (_recentActions.Count > 12) _recentActions.RemoveRange(0, _recentActions.Count - 12);
    }

    private Decision? BuildDecision(RunState run)
    {
        NRun? runNode = NGame.Instance?.CurrentRunNode;
        if (runNode is not null && NMapScreen.Instance?.IsOpen == true)
        {
            Decision? mapDecision = MapDecision(runNode, run);
            if (mapDecision is not null) return mapDecision;
        }
        if (NOverlayStack.Instance?.Peek() is Node overlay) return OverlayDecision(overlay);
        if (CombatManager.Instance.IsInProgress) return CombatDecision(run);
        if (runNode is null) return null;
        return RoomDecision(runNode, run) ?? MapDecision(runNode, run);
    }

    private Decision? CombatDecision(RunState run)
    {
        Player? player = LocalContext.GetMe(run);
        PlayerCombatState? pcs = player?.PlayerCombatState;
        ICombatState? combat = player?.Creature.CombatState;
        if (player is null || pcs?.Phase != PlayerTurnPhase.Play || combat is null) return null;
        string actionState = CombatActionStateToken(player, combat);
        List<CombatBinding> bindings = BuildCombatBindings(player, actionState);
        bindings.Add(new(new($"combat:{actionState}:end", "end_turn", "End turn"), null, null, null));
        var hand = pcs.Hand.Cards.Select((card, index) => CardObservation(card, $"h{index}", PileType.Hand)).ToList();
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
            runObjective = new
            {
                goal = "Survive the act, reach the act boss, and defeat the boss",
                currentAct = run.CurrentActIndex + 1,
                actFloor = run.ActFloor,
                totalFloor = run.TotalFloor,
                hpIsScarce = true,
                normalRecovery = "Rest sites/fires are the primary HP recovery source"
            },
            rules = new
            {
                baseCardsDrawnAtTurnStart = 5,
                handLimit = CardPile.MaxCardsInHand,
                drawPileOrderIsKnown = false,
                oneActionIsChosenPerRequest = true
            },
            turn = pcs.TurnNumber,
            round = combat.RoundNumber,
            phase = pcs.Phase.ToString(),
            player = new
            {
                character = new
                {
                    id = player.Character.Id.Entry,
                    name = SafeText(() => player.Character.Title.GetFormattedText())
                },
                hp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                block = player.Creature.Block,
                energy = pcs.Energy,
                maxEnergy = pcs.MaxEnergy,
                stars = pcs.Stars,
                powers = PowerObservation(player.Creature),
                relics = player.Relics.Select(relic => new
                {
                    id = relic.Id.Entry,
                    title = SafeText(() => relic.Title.GetFormattedText()),
                    description = SafeText(() => relic.DynamicDescription.GetFormattedText()),
                    counter = relic.ShowCounter ? relic.DisplayAmount : null as int?
                }).ToList()
            },
            piles = new
            {
                draw = PileObservation(pcs.DrawPile, orderKnown: false),
                discard = PileObservation(pcs.DiscardPile, orderKnown: false),
                exhaust = PileObservation(pcs.ExhaustPile, orderKnown: false),
                play = PileObservation(pcs.PlayPile, orderKnown: true)
            },
            masterDeck = PileObservation(player.Deck, orderKnown: false),
            hand,
            potions,
            cardsPlayedThisTurn = CombatManager.Instance.History.CardPlaysFinished
                .Where(entry => entry.Actor == player.Creature && entry.HappenedThisTurn(combat))
                .Select(entry => new
                {
                    id = entry.CardPlay.Card.Id.Entry,
                    title = entry.CardPlay.Card.Title,
                    target = entry.CardPlay.Target?.Name
                }).ToList(),
            enemies = combat.Enemies.Select((enemy, index) => new
            {
                key = $"e{index}",
                model = enemy.ModelId.Entry,
                name = enemy.Name,
                hp = enemy.CurrentHp,
                maxHp = enemy.MaxHp,
                block = enemy.Block,
                alive = enemy.IsAlive,
                hittable = enemy.IsHittable,
                powers = PowerObservation(enemy),
                nextMove = EnemyForecast(enemy, combat),
                intents = enemy.Monster?.NextMove.Intents.Select(intent => IntentObservation(intent, combat, enemy)).ToList()
            }).ToList()
        };
        if (_config.Verbose) LogCombatState("decision", player, combat);
        return new("combat", observation, bindings.Select(binding => binding.Action).ToList());
    }

    private static List<CombatBinding> BuildCombatBindings(Player player, string actionState)
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
                var action = new AgentAction($"combat:{actionState}:card:h{handIndex}:none", "play_card", $"Play h{handIndex} {card.Title}");
                bindings.Add(new(action, card, null, null));
            }
            for (int targetIndex = 0; targetIndex < combat.Creatures.Count; targetIndex++)
            {
                Creature target = combat.Creatures[targetIndex];
                if (!target.IsHittable || !card.IsValidTarget(target)) continue;
                var action = new AgentAction($"combat:{actionState}:card:h{handIndex}:t{targetIndex}", "play_card", $"Play h{handIndex} {card.Title} on {target.Name}");
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
                var action = new AgentAction($"combat:{actionState}:potion:s{slotIndex}:none", "use_potion", $"Use s{slotIndex} {title}");
                bindings.Add(new(action, null, potion, null));
            }
            for (int targetIndex = 0; targetIndex < combat.Creatures.Count; targetIndex++)
            {
                Creature target = combat.Creatures[targetIndex];
                if (!potion.IsValidTarget(target)) continue;
                var action = new AgentAction($"combat:{actionState}:potion:s{slotIndex}:t{targetIndex}", "use_potion", $"Use s{slotIndex} {title} on {target.Name}");
                bindings.Add(new(action, null, potion, target));
            }
        }
        return bindings;
    }

    private static string CombatActionStateToken(Player player, ICombatState combat)
    {
        PlayerCombatState state = player.PlayerCombatState!;
        string hand = string.Join(',', state.Hand.Cards.Select(card => $"{card.Id.Entry}+{card.CurrentUpgradeLevel}"));
        string enemies = string.Join(',', combat.Creatures.Select(creature => $"{creature.CombatId}:{creature.CurrentHp}:{creature.Block}:{creature.IsAlive}"));
        string source = $"turn={state.TurnNumber};round={combat.RoundNumber};phase={state.Phase};energy={state.Energy};stars={state.Stars};hp={player.Creature.CurrentHp};block={player.Creature.Block};hand={hand};enemies={enemies}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];
    }

    private static bool CanUsePotion(Player player, PotionModel potion) =>
        !potion.IsQueued && !potion.HasBeenRemovedFromState && player.Creature.IsAlive && player.CanRemovePotions && potion.PassesCustomUsabilityCheck && potion.Usage is PotionUsage.CombatOnly or PotionUsage.AnyTime;

    private static List<(CardModel Card, AgentAction Action)> StableCardActions(IEnumerable<CardModel> cards, string prefix)
    {
        Dictionary<string, int> occurrences = [];
        var result = new List<(CardModel, AgentAction)>();
        foreach (CardModel card in cards)
        {
            string model = card.Id.Entry;
            int occurrence = occurrences.GetValueOrDefault(model);
            occurrences[model] = occurrence + 1;
            string id = $"{prefix}:{model}:{occurrence}";
            result.Add((card, new AgentAction(id, "choose_card", $"Choose {card.Title}")));
        }
        return result;
    }

    private static object CardObservation(CardModel card, string key, PileType pile) => new
    {
        key,
        model = card.Id.Entry,
        title = card.Title,
        type = card.Type.ToString(),
        rarity = card.Rarity.ToString(),
        cost = card.EnergyCost.CostsX ? "X" : card.EnergyCost.GetWithModifiers(CostModifiers.All).ToString(),
        upgraded = card.IsUpgraded,
        keywords = card.Keywords.Select(keyword => keyword.ToString()).ToList(),
        description = SafeText(() => card.GetDescriptionForPile(pile)),
        playable = pile == PileType.Hand ? card.CanPlay(out _, out _) : null as bool?
    };

    private static object PileObservation(CardPile pile, bool orderKnown) => new
    {
        count = pile.Cards.Count,
        orderKnown,
        cards = pile.Cards
            .OrderBy(card => card.Id.Entry)
            .ThenBy(card => card.CurrentUpgradeLevel)
            .Select((card, index) => CardObservation(card, $"{pile.Type.ToString().ToLowerInvariant()}{index}", pile.Type))
            .ToList()
    };

    private static object PowerObservation(Creature creature) => creature.Powers.Where(power => power.IsVisible).Select(power => new
    {
        id = power.Id.Entry,
        title = SafeText(() => power.Title.GetFormattedText()),
        description = SafeText(() => PowerDescription(power)),
        amount = power.DisplayAmount,
        type = power.TypeForCurrentAmount.ToString()
    }).ToList();

    private static string PowerDescription(PowerModel power)
    {
        LocString description = power.HasSmartDescription ? power.SmartDescription : power.Description;
        int playerCount = power.Owner.CombatState?.Players.Count ?? 1;
        description.Add("Amount", power.Amount);
        description.Add("OnPlayer", power.Owner.IsPlayer);
        description.Add("IsMultiplayer", playerCount > 1);
        description.Add("PlayerCount", playerCount);
        power.DynamicVars.AddTo(description);
        return description.GetFormattedText();
    }

    private static object IntentObservation(AbstractIntent intent, ICombatState combat, Creature owner)
    {
        AttackIntent? attack = intent as AttackIntent;
        return new
        {
            type = intent.IntentType.ToString(),
            label = SafeText(() => intent.GetIntentLabel(combat.Allies, owner).GetFormattedText()),
            amountKnown = attack is not null || intent is StatusIntent,
            damagePerHit = attack is null ? null : SafeInt(() => attack.GetSingleDamage(combat.Allies, owner)),
            hits = attack is null ? null : (int?)Math.Max(1, attack.Repeats),
            totalDamage = attack is null ? null : SafeInt(() => attack.GetTotalDamage(combat.Allies, owner)),
            statusCards = (intent as StatusIntent)?.CardCount
        };
    }

    private static object? EnemyForecast(Creature enemy, ICombatState combat)
    {
        if (enemy.Monster?.NextMove is not { } move) return null;
        List<AbstractIntent> intents = move.Intents.ToList();
        List<AttackIntent> attacks = intents.OfType<AttackIntent>().ToList();
        return new
        {
            moveId = move.StateId,
            attack = new
            {
                amountKnown = attacks.Count > 0,
                totalDamage = attacks.Count == 0 ? null : (int?)attacks.Sum(attack => SafeInt(() => attack.GetTotalDamage(combat.Allies, enemy)) ?? 0),
                parts = attacks.Select(attack => new
                {
                    damagePerHit = SafeInt(() => attack.GetSingleDamage(combat.Allies, enemy)),
                    hits = Math.Max(1, attack.Repeats),
                    totalDamage = SafeInt(() => attack.GetTotalDamage(combat.Allies, enemy))
                }).ToList()
            },
            defend = new { present = intents.Any(intent => intent is DefendIntent), amountKnown = false, amount = null as int? },
            heal = new { present = intents.Any(intent => intent is HealIntent), amountKnown = false, amount = null as int? },
            buff = new { present = intents.Any(intent => intent is BuffIntent), amountKnown = false, amount = null as int? },
            debuff = new { present = intents.Any(intent => intent is DebuffIntent or CardDebuffIntent), amountKnown = false },
            statusCards = intents.OfType<StatusIntent>().Sum(intent => intent.CardCount),
            summons = intents.Any(intent => intent is SummonIntent),
            stun = intents.Any(intent => intent is StunIntent),
            hidden = intents.Any(intent => intent is HiddenIntent or UnknownIntent)
        };
    }

    private static string? SafeText(Func<string> getText) { try { return getText(); } catch (Exception) { return null; } }
    private static int? SafeInt(Func<int> getValue) { try { return getValue(); } catch (Exception) { return null; } }

    private Decision? MapDecision(NRun runNode, RunState run)
    {
        if (NMapScreen.Instance?.IsOpen != true || !runNode.GlobalUi.MapScreen.IsVisibleInTree()) return null;
        List<NMapPoint> points = GetEnabledMapPoints(runNode);
        return points.Count == 0 ? null : new("map", new
        {
            floor = run.TotalFloor,
            actFloor = run.ActFloor,
            current = run.CurrentMapCoord?.ToString(),
            visited = run.VisitedMapCoords.Count
        }, points.Select(p => new AgentAction($"map:r{p.Point.coord.row}:c{p.Point.coord.col}", "map_path", $"Choose {p.Point.PointType} at row {p.Point.coord.row}, column {p.Point.coord.col}")).ToList());
    }

    private static List<NMapPoint> GetEnabledMapPoints(NRun runNode) => UiHelper.FindAll<NMapPoint>(runNode.GlobalUi.MapScreen)
        .Where(point => point.IsEnabled && point.IsVisibleInTree())
        .OrderBy(point => point.Point.coord.row)
        .ThenBy(point => point.Point.coord.col)
        .ToList();

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
            bool inventoryOpen = shop.Inventory.IsOpen;
            List<NMerchantSlot> stocked = inventoryOpen ? shop.Inventory.GetAllSlots().Where(slot => slot is not NMerchantCardRemoval && slot.Entry.IsStocked).ToList() : [];
            List<MerchantBinding> bindings = StableMerchantBindings(stocked.Where(slot => slot.Entry.EnoughGold));
            var actions = bindings.Select(binding => binding.Action).ToList();
            if (inventoryOpen) actions.Add(new("close", "close_shop", "Close merchant inventory"));
            else actions.Add(new("open", "open_shop", "Open merchant inventory"));
            if (!inventoryOpen && shop.ProceedButton.IsEnabled) actions.Add(new("proceed", "proceed", "Leave shop"));
            return new("shop", new
            {
                gold = player.Gold,
                hp = player.Creature.CurrentHp,
                maxHp = player.Creature.MaxHp,
                floor = run.TotalFloor,
                items = stocked.Select(MerchantObservation).ToList(),
                deck = player.Deck.Cards.Select((card, index) => CardObservation(card, $"deck{index}", PileType.Deck)).ToList(),
                relics = player.Relics.Select(relic => new { id = relic.Id.Entry, title = SafeText(() => relic.Title.GetFormattedText()), description = SafeText(() => relic.DynamicDescription.GetFormattedText()) }).ToList(),
                potions = player.Potions.Select(potion => new { id = potion.Id.Entry, title = SafeText(() => potion.Title.GetFormattedText()), description = SafeText(() => potion.DynamicDescription.GetFormattedText()) }).ToList()
            }, actions);
        }
        return null;
    }

    private static List<MerchantBinding> StableMerchantBindings(IEnumerable<NMerchantSlot> slots)
    {
        Dictionary<string, int> occurrences = [];
        var bindings = new List<MerchantBinding>();
        foreach (NMerchantSlot slot in slots)
        {
            string model = MerchantModelId(slot.Entry);
            string key = $"{slot.Entry.GetType().Name}:{model}:{slot.Entry.Cost}";
            int occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            AgentAction action = new($"buy:{key}:{occurrence}", "buy", $"Buy {MerchantTitle(slot.Entry)} for {slot.Entry.Cost} gold");
            bindings.Add(new(action, slot));
        }
        return bindings;
    }

    private static object MerchantObservation(NMerchantSlot slot) => new
    {
        id = MerchantModelId(slot.Entry),
        kind = slot.Entry.GetType().Name,
        title = MerchantTitle(slot.Entry),
        description = MerchantDescription(slot.Entry),
        cost = slot.Entry.Cost,
        affordable = slot.Entry.EnoughGold,
        stocked = slot.Entry.IsStocked
    };

    private static string MerchantModelId(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card => card.CreationResult?.Card.Id.Entry ?? "sold",
        MerchantRelicEntry relic => relic.Model?.Id.Entry ?? "sold",
        MerchantPotionEntry potion => potion.Model?.Id.Entry ?? "sold",
        _ => entry.GetType().Name
    };

    private static string MerchantTitle(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card => card.CreationResult?.Card.Title ?? "Sold card",
        MerchantRelicEntry relic => SafeText(() => relic.Model?.Title.GetFormattedText() ?? "Sold relic") ?? "Relic",
        MerchantPotionEntry potion => SafeText(() => potion.Model?.Title.GetFormattedText() ?? "Sold potion") ?? "Potion",
        _ => entry.GetType().Name
    };

    private static string? MerchantDescription(MerchantEntry entry) => entry switch
    {
        MerchantCardEntry card when card.CreationResult?.Card is CardModel model => SafeText(() => model.GetDescriptionForPile(PileType.Deck)),
        MerchantRelicEntry relic when relic.Model is not null => SafeText(() => relic.Model.DynamicDescription.GetFormattedText()),
        MerchantPotionEntry potion when potion.Model is not null => SafeText(() => potion.Model.DynamicDescription.GetFormattedText()),
        _ => null
    };

    private Decision? OverlayDecision(Node overlay)
    {
        List<NCardHolder> cards = UiHelper.FindAll<NCardHolder>(overlay).Where(holder => holder.IsVisibleInTree() && holder.CardModel is not null).ToList();
        if (cards.Count > 0)
        {
            List<(CardModel Card, AgentAction Action)> stableCards = StableCardActions(cards.Select(holder => holder.CardModel!), "choice");
            List<AgentAction> actions = stableCards.Select(binding => binding.Action).ToList();
            if (UiHelper.FindAll<NConfirmButton>(overlay).Any(b => b.IsEnabled)) actions.Add(new("confirm", "confirm", "Confirm selection"));
            if (UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).Any(b => b.IsEnabled)) actions.Add(new("skip", "skip", "Skip"));
            return new(overlay.GetType().Name, new
            {
                screen = overlay.GetType().Name,
                cards = stableCards.Select(binding => CardObservation(binding.Card, binding.Action.Id, binding.Card.Pile?.Type ?? PileType.None)).ToList()
            }, actions);
        }
        List<RewardBinding> rewards = StableRewardBindings(overlay);
        if (rewards.Count > 0) return new("rewards", new { screen = overlay.GetType().Name }, rewards.Select(binding => binding.Action).ToList());
        var proceed = UiHelper.FindAll<NProceedButton>(overlay).Where(b => b.IsEnabled).ToList();
        if (proceed.Count > 0) return new("proceed", new { screen = overlay.GetType().Name }, new[] { new AgentAction("proceed", "proceed", "Proceed") });
        var clickables = UiHelper.FindAll<NClickableControl>(overlay).Where(b => b.IsEnabled && b.Visible).ToList();
        return clickables.Count == 0 ? null : new("generic_overlay", new { screen = overlay.GetType().Name, gameText = "untrusted" }, clickables.Select((b, i) => new AgentAction("g" + i, "click", b.GetType().Name)).ToList());
    }

    private static List<RewardBinding> StableRewardBindings(Node overlay)
    {
        Dictionary<string, int> occurrences = [];
        var bindings = new List<RewardBinding>();
        foreach (NRewardButton button in UiHelper.FindAll<NRewardButton>(overlay).Where(button => button.IsEnabled && button.IsVisibleInTree()))
        {
            string type = button.Reward?.GetType().Name ?? "UnknownReward";
            int occurrence = occurrences.GetValueOrDefault(type);
            occurrences[type] = occurrence + 1;
            bindings.Add(new(new AgentAction($"reward:{type}:{occurrence}", "claim_reward", $"Claim {type}"), button));
        }
        return bindings;
    }

    private async Task ResolveDecisionAsync(Decision decision, string? response)
    {
        AgentAction? action;
        if (!DecisionProtocol.TryParseAndValidate(response, decision.Actions, out ModelDecision chosen))
        {
            action = decision.Screen == "combat"
                ? decision.Actions.FirstOrDefault(candidate => candidate.Kind == "play_card")
                    ?? decision.Actions.FirstOrDefault(candidate => candidate.Kind == "use_potion")
                    ?? decision.Actions.FirstOrDefault(candidate => candidate.Kind == "end_turn")
                : DecisionPolicy.ConservativeFallback(decision);
            if (action is null)
            {
                if (_config.Verbose) GD.Print($"[Sts2LlmAgent] {decision.Screen}: no safe fallback; waiting");
                return;
            }
            chosen = new(action.Id, "fallback");
        }
        else action = decision.Actions.Single(candidate => candidate.Id == chosen.ActionId);
        if (_config.Verbose && chosen.Reason.Length > 0) GD.Print($"[Sts2LlmAgent] {decision.Screen}: {chosen.Reason}");
        string before = DecisionProtocol.BuildUserJson(decision with { Memory = null, RecentActions = null });
        await ExecuteAsync(decision.Screen, action).WaitAsync(TimeSpan.FromSeconds(15), _lifetime.Token);
        for (int frame = 0; frame < 3; frame++) await FrameAsync();
        RunState? run = RunManager.Instance?.DebugOnlyGetState();
        Decision? afterDecision = run is null || run.Players.Count != 1 ? null : BuildDecision(run);
        string? after = afterDecision is null ? null : DecisionProtocol.BuildUserJson(afterDecision with { Memory = null, RecentActions = null });
        if (after != before)
        {
            AddRecentAction(new(decision.Screen, action.Id, action.Kind, action.Label, SummarizeCurrentState(run)));
        }
    }

    private static string SummarizeCurrentState(RunState? run)
    {
        if (run is null) return "run ended or returned to menu";
        Player? player = LocalContext.GetMe(run);
        if (player is null) return $"act={run.CurrentActIndex + 1} floor={run.TotalFloor}";
        string summary = $"act={run.CurrentActIndex + 1} floor={run.TotalFloor} hp={player.Creature.CurrentHp}/{player.Creature.MaxHp}";
        if (player.PlayerCombatState is PlayerCombatState state)
        {
            summary += $" turn={state.TurnNumber} energy={state.Energy} block={player.Creature.Block} hand={state.Hand.Cards.Count}";
        }
        return summary;
    }

    private async Task ExecuteAsync(string screen, AgentAction action)
    {
        await FrameAsync();
        RunState? liveRun = RunManager.Instance?.DebugOnlyGetState();
        Decision? liveDecision = liveRun is null || liveRun.Players.Count != 1 ? null : BuildDecision(liveRun);
        AgentAction? liveAction = liveDecision?.Screen == screen ? liveDecision.Actions.SingleOrDefault(candidate => candidate.Id == action.Id && candidate.Kind == action.Kind) : null;
        if (liveAction is null) return;
        if (screen == "combat") { await ExecuteCombat(liveAction); return; }
        NRun? run = NGame.Instance?.CurrentRunNode;
        if (run is null) return;
        action = liveAction;
        if (screen == "map")
        {
            NMapPoint? point = GetEnabledMapPoints(run).SingleOrDefault(point => action.Id == $"map:r{point.Point.coord.row}:c{point.Point.coord.col}");
            if (point is not null) await UiHelper.Click(point);
            return;
        }
        if (NOverlayStack.Instance?.Peek() is Node overlay) { await ExecuteOverlayAsync(overlay, liveAction); return; }
        Node room = run.GetNode("RoomContainer"); int index = Index(action.Id);
        if (screen == "event") { var items = UiHelper.FindAll<NEventOptionButton>(room).Where(o => o.IsEnabled && !o.Option.IsLocked).ToList(); if (index >= 0 && index < items.Count) await UiHelper.Click(items[index]); }
        else if (screen == "rest_site") { var rest = room.GetNodeOrNull<NRestSiteRoom>("RestSiteRoom"); if (action.Kind == "proceed" && rest?.ProceedButton.IsEnabled == true) await UiHelper.Click(rest.ProceedButton); else { var items = UiHelper.FindAll<NRestSiteButton>(room).Where(b => b.Option.IsEnabled).ToList(); if (index >= 0 && index < items.Count) await UiHelper.Click(items[index]); } }
        else if (screen == "treasure") { var treasure = room.GetNodeOrNull<NTreasureRoom>("TreasureRoom"); if (treasure is null) return; if (action.Id == "open") { var chest = treasure.GetNodeOrNull<NClickableControl>("Chest"); if (chest?.IsEnabled == true) await UiHelper.Click(chest); } else if (action.Kind == "claim_treasure") { var holders = UiHelper.FindAll<NTreasureRoomRelicHolder>(treasure).Where(h => h.IsEnabled && h.Visible).ToList(); if (index >= 0 && index < holders.Count) await UiHelper.Click(holders[index]); } else if (treasure.ProceedButton.IsEnabled) await UiHelper.Click(treasure.ProceedButton); }
        else if (screen == "shop")
        {
            var shop = room.GetNodeOrNull<NMerchantRoom>("MerchantRoom");
            if (shop is null) return;
            if (action.Kind == "buy")
            {
                MerchantBinding? binding = StableMerchantBindings(shop.Inventory.GetAllSlots().Where(slot => slot is not NMerchantCardRemoval && slot.Entry.IsStocked && slot.Entry.EnoughGold)).SingleOrDefault(binding => binding.Action.Id == action.Id);
                if (binding is not null) await binding.Slot.Entry.OnTryPurchaseWrapper(shop.Inventory.Inventory);
            }
            else if (action.Kind == "open_shop" && !shop.Inventory.IsVisibleInTree()) shop.OpenInventory();
            else if (action.Kind == "close_shop")
            {
                NBackButton? closeButton = shop.Inventory.GetNodeOrNull<NBackButton>("%BackButton");
                if (closeButton?.IsEnabled == true) closeButton.ForceClick();
                else shop.Inventory.CallDeferred(NMerchantInventory.MethodName.Close);
                await WaitHelper.Until(() => !shop.Inventory.IsOpen, _lifetime.Token, TimeSpan.FromSeconds(5), "Merchant inventory did not close");
            }
            else if (shop.ProceedButton.IsEnabled) await UiHelper.Click(shop.ProceedButton);
        }
    }

    private async Task ExecuteCombat(AgentAction action)
    {
        RunState? run = RunManager.Instance?.DebugOnlyGetState(); if (run is null || run.Players.Count != 1) return;
        Player? player = LocalContext.GetMe(run); if (player?.PlayerCombatState?.Phase != PlayerTurnPhase.Play) return;
        ICombatState? combat = player.Creature.CombatState;
        if (combat is null) return;
        string currentState = CombatActionStateToken(player, combat);
        if (!action.Id.StartsWith($"combat:{currentState}:", StringComparison.Ordinal))
        {
            GD.PrintErr($"[Sts2LlmAgent] discarded stale combat action {action.Id}; current turn={player.PlayerCombatState.TurnNumber} energy={player.PlayerCombatState.Energy} hand={player.PlayerCombatState.Hand.Cards.Count}");
            return;
        }
        if (_config.Verbose) LogCombatState($"before {action.Id}", player, combat);
        if (action.Kind == "end_turn")
        {
            PlayerCmd.EndTurn(player, false);
            if (_config.Verbose)
            {
                for (int frame = 0; frame < 5; frame++) await FrameAsync();
                LogCombatState($"after {action.Id}", player, combat);
            }
            return;
        }
        CombatBinding? binding = BuildCombatBindings(player, currentState).SingleOrDefault(candidate => candidate.Action.Id == action.Id && candidate.Action.Kind == action.Kind);
        if (binding?.Card is CardModel card)
        {
            using IDisposable selector = CardSelectCmd.PushSelector(new LlmCardSelector(this));
            if (!card.TryManualPlay(binding.Target)) return;
            ulong deadline = Time.GetTicksMsec() + 30_000;
            while (Time.GetTicksMsec() < deadline && player.PlayerCombatState?.Phase == PlayerTurnPhase.Play)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                PileType? pile = card.Pile?.Type;
                if (pile is not (PileType.Hand or PileType.Play)) break;
                await FrameAsync();
            }
        }
        else if (binding?.Potion is PotionModel potion && CanUsePotion(player, potion) && potion.IsValidTarget(binding.Target)) potion.EnqueueManualUse(binding.Target);
        if (_config.Verbose)
        {
            for (int frame = 0; frame < 5; frame++) await FrameAsync();
            LogCombatState($"after {action.Id}", player, combat);
        }
    }

    private static void LogCombatState(string label, Player player, ICombatState combat)
    {
        PlayerCombatState state = player.PlayerCombatState!;
        string enemies = string.Join("; ", combat.Enemies.Select((enemy, index) =>
        {
            List<AbstractIntent> intents = enemy.Monster?.NextMove.Intents.ToList() ?? [];
            List<AttackIntent> attacks = intents.OfType<AttackIntent>().ToList();
            int? damage = attacks.Count == 0 ? null : attacks.Sum(attack => SafeInt(() => attack.GetTotalDamage(combat.Allies, enemy)) ?? 0);
            string values = damage.HasValue ? $"attack={damage.Value}" : string.Join('+', intents.Select(intent => intent.IntentType));
            return $"e{index}:{enemy.Name} hp={enemy.CurrentHp}/{enemy.MaxHp} block={enemy.Block} move={enemy.Monster?.NextMove.StateId} next={values}";
        }));
        GD.Print($"[Sts2LlmAgent] state {label}: player hp={player.Creature.CurrentHp}/{player.Creature.MaxHp} block={player.Creature.Block} energy={state.Energy}/{state.MaxEnergy} hand={state.Hand.Cards.Count} draw={state.DrawPile.Cards.Count} discard={state.DiscardPile.Cards.Count} exhaust={state.ExhaustPile.Cards.Count} enemies=[{enemies}]");
    }

    private async Task ExecuteOverlayAsync(Node overlay, AgentAction action)
    {
        if (action.Kind == "skip") { UiHelper.FindAll<NChoiceSelectionSkipButton>(overlay).FirstOrDefault(b => b.IsEnabled)?.ForceClick(); }
        else if (action.Kind == "confirm") { UiHelper.FindAll<NConfirmButton>(overlay).FirstOrDefault(b => b.IsEnabled)?.ForceClick(); }
        else if (action.Kind == "choose_card")
        {
            List<NCardHolder> holders = UiHelper.FindAll<NCardHolder>(overlay).Where(holder => holder.IsVisibleInTree() && holder.CardModel is not null).ToList();
            List<(CardModel Card, AgentAction Action)> cards = StableCardActions(holders.Select(holder => holder.CardModel!), "choice");
            int index = cards.FindIndex(binding => binding.Action.Id == action.Id);
            if (index >= 0)
            {
                holders[index].EmitSignal(NCardHolder.SignalName.Pressed, holders[index]);
                await CompleteCardSelectionAsync(overlay);
            }
        }
        else if (action.Kind == "claim_reward") StableRewardBindings(overlay).SingleOrDefault(binding => binding.Action.Id == action.Id)?.Button.ForceClick();
        else if (action.Kind == "click") { int i = Index(action.Id); var buttons = UiHelper.FindAll<NClickableControl>(overlay).Where(b => b.IsEnabled && b.Visible).ToList(); if (i >= 0 && i < buttons.Count) buttons[i].ForceClick(); }
        else { var button = UiHelper.FindAll<NProceedButton>(overlay).FirstOrDefault(b => b.IsEnabled); button?.ForceClick(); }
    }

    private async Task CompleteCardSelectionAsync(Node originalOverlay)
    {
        for (int stage = 0; stage < 2; stage++)
        {
            NConfirmButton? confirm = null;
            ulong deadline = Time.GetTicksMsec() + 3_000;
            while (Time.GetTicksMsec() < deadline && GodotObject.IsInstanceValid(originalOverlay) && originalOverlay.IsInsideTree())
            {
                confirm = UiHelper.FindAll<NConfirmButton>(originalOverlay)
                    .FirstOrDefault(button => button.IsEnabled && button.IsVisibleInTree());
                if (confirm is not null) break;
                await FrameAsync();
            }
            if (confirm is null) return;
            confirm.ForceClick();
            for (int frame = 0; frame < 5; frame++) await FrameAsync();
            if (!GodotObject.IsInstanceValid(originalOverlay) || !originalOverlay.IsInsideTree() || NOverlayStack.Instance?.Peek() != originalOverlay) return;
        }
    }

    private static int Index(string id) => int.TryParse(id.AsSpan(1), out int value) ? value : -1;
    private async Task FrameAsync() { await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame); }
}
