using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.ValueProps;
using PerkTree.Mod.Effects;

namespace PerkTree.Mod;

/// <summary>
/// One player's perk host. It is not a relic or a power: the game calls its hooks because the mod subscribed it
/// with ModHelper.SubscribeForRunStateHooks, so it never shows up in the relic bar and cannot collide with other
/// mods' relic logic.
/// </summary>
internal sealed class PerkHostModel : AbstractModel
{
    public Player Owner { get; private set; }
    public PerkState State { get; private set; }

    /// <summary>The only source of randomness for this player's perks (the game's seeded RNG; see PerkRng).</summary>
    public PerkRng Rolls { get; private set; }

    // Set at the player's FIRST vote on a map and held until the next room is entered. Votes can be changed or
    // cancelled in the game; if cancelling released the lock, "vote, respec, cancel" would defeat the lock rule.
    private bool _votedThisMap;

    // The same room can reach a hook twice if a model is listed by both the run and combat iteration; this stops
    // a double point award.
    private CombatRoom? _lastAwardedRoom;

    /// <summary>
    /// The game builds exactly ONE instance of every AbstractModel subclass itself (ModelDb, with a public parameterless
    /// constructor) and the base constructor registers the model's id, so a second `new PerkHostModel(...)` throws
    /// DuplicateModelException (seen in the log when a run started). That one instance is the canonical placeholder: never
    /// subscribed to hooks. Per-player hosts are made with <see cref="Create"/>, which clones it the way the game makes
    /// its own mutable models.
    /// </summary>
    public PerkHostModel()
    {
        Owner = null!;
        State = null!;
        Rolls = null!;
    }

    public static PerkHostModel Create(Player owner, PerkState state)
    {
        var canonical = ModelDb.GetById<PerkHostModel>(ModelDb.GetId(typeof(PerkHostModel)));
        var host = (PerkHostModel)canonical.MutableClone();
        host.Attach(owner, state);
        return host;
    }

    // A clone is a shallow copy, so every reference-type field would be SHARED with the canonical instance and with the other
    // players' hosts. Give this host its own of everything.
    private void Attach(Player owner, PerkState state)
    {
        Owner = owner;
        State = state;
        Rolls = new PerkRng(owner);
        _votedThisMap = false;
        _lastAwardedRoom = null;
        _effective = new(StringComparer.Ordinal);
        _rolls = new(StringComparer.Ordinal);
        _caps = new(StringComparer.Ordinal);
        _effectiveVersion = -1;
        _handlers = new(StringComparer.Ordinal);
        _active = Array.Empty<EffectHandler>();
        _damageMods = Array.Empty<EffectHandler>();
        _incomingMods = Array.Empty<EffectHandler>();
        _activeVersion = -1;
        _warned = new(StringComparer.Ordinal);
        ConvertingVigor = false;
        TurnEnd = new();
        _potionStartedInCombat = false;
    }

    // Combat hooks (card played, turn start...) are needed by later effects. Room-level hooks are delivered to
    // every model regardless.
    public override bool ShouldReceiveCombatHooks => true;

    // Per-effect committed totals, rebuilt only when the committed set changes. The resist patches read these on every
    // damage/block calculation, so it must be a dictionary lookup, not a re-sum of the tree.
    private Dictionary<string, decimal> _effective = new(StringComparer.Ordinal);
    private Dictionary<string, IReadOnlyList<NodeRoll>> _rolls = new(StringComparer.Ordinal);
    private Dictionary<string, decimal?> _caps = new(StringComparer.Ordinal);
    private int _effectiveVersion = -1;

    private void RefreshTotals()
    {
        if (_effectiveVersion == State.CommittedVersion) return;
        _effective.Clear();
        _rolls.Clear();
        _caps.Clear();
        foreach (var total in State.GetTotals().Values)
        {
            _effective[total.EffectId] = total.Effective;
            _rolls[total.EffectId] = total.NodeRolls;
            _caps[total.EffectId] = total.Cap;
        }
        _effectiveVersion = State.CommittedVersion;
    }

    /// <summary>The player's committed (active) total for an effect, already clamped to its cap. 0 if none.</summary>
    public decimal Effective(string effectId)
    {
        RefreshTotals();
        return _effective.TryGetValue(effectId, out var v) ? v : 0m;
    }

    public void OpenWindow()
    {
        if (!_votedThisMap) State.OpenAllocationWindow();
    }

    /// <summary>
    /// The local player voted: stop edits now and send the lock. The commit itself is applied by the ordered
    /// <see cref="CommitPerksAction"/> on every client, so it lands at the same point everywhere.
    /// Only the first vote counts; later votes and cancels must not re-send or reopen anything.
    /// </summary>
    public void LockForVote()
    {
        if (_votedThisMap) return;
        _votedThisMap = true;
        State.CloseAllocationWindow();
        PerkNet.RequestCommit(this);
    }

    // ---- effect handlers ----------------------------------------------------------------------------------------

    // One handler instance per effect, kept for the whole run so its counters survive; _active is the subset with a
    // committed total above zero, sorted by id so every client walks them in the same order.
    private Dictionary<string, EffectHandler> _handlers = new(StringComparer.Ordinal);
    private EffectHandler[] _active = Array.Empty<EffectHandler>();
    private EffectHandler[] _damageMods = Array.Empty<EffectHandler>(); // the active handlers that change outgoing damage (hot path)
    private EffectHandler[] _incomingMods = Array.Empty<EffectHandler>(); // ... and incoming damage (hot path)
    private int _activeVersion = -1;
    private HashSet<string> _warned = new(StringComparer.Ordinal);

    private EffectHandler[] Active
    {
        get
        {
            if (_activeVersion == State.CommittedVersion) return _active;

            var ids = new List<string>();
            foreach (var total in State.GetTotals().Values)
                if (total.Effective > 0) ids.Add(total.EffectId);
            ids.Sort(StringComparer.Ordinal);

            var active = new List<EffectHandler>(ids.Count);
            foreach (var id in ids)
            {
                if (!_handlers.TryGetValue(id, out var handler))
                {
                    handler = HandlerRegistry.Create(id);
                    if (handler == null)
                    {
                        // Not an error for patch-based effects; for anything else it means a tree uses an effect that
                        // has no code yet, which the player should be able to find in the log.
                        if (!HandlerRegistry.IsPatchHandled(id) && _warned.Add(id))
                            MainFile.LogWarn($"effect '{id}' is committed but has no handler; it does nothing");
                        continue;
                    }
                    handler.Host = this;
                    _handlers[id] = handler;
                }
                active.Add(handler);
            }
            _active = active.ToArray();
            _damageMods = active.Where(h => h.ModifiesDamage).ToArray();
            _incomingMods = active.Where(h => h.ModifiesIncomingDamage).ToArray();
            _activeVersion = State.CommittedVersion;
            return _active;
        }
    }

    /// <summary>
    /// What an effect gives on this trigger, after rolling its chances: the sum of the amounts of the perks that succeeded.
    /// Every perk (node) rolls ON ITS OWN, so three 50% perks are three rolls, and a perk whose chance is past 100% gives its
    /// whole part as sure successes plus a roll for the rest (150% = one sure success and a 50% roll). A perk at exactly 100%
    /// always succeeds and uses no random numbers, so an effect nobody has made chancy behaves as a plain sum.
    ///
    /// The luck perks only touch perks that are actually chancy (chance below 100%): proc_boost / proc_double change that
    /// perk's chance first, and proc_reroll gives each failed roll extra tries. Guaranteed perks stay guaranteed.
    /// Every roll comes from the game's seeded RNG (PerkRng) in a fixed order (perks sorted by node id), so all clients agree.
    /// Successes are capped at <see cref="ChanceMath.MaxGuaranteed"/> in total so a huge stack can't become an endless loop.
    /// </summary>
    public decimal Roll(string effectId, int extraTries = 0)
    {
        RefreshTotals();
        if (!_rolls.TryGetValue(effectId, out var perPerk)) return 0m;

        var boost = Effective("proc_boost");
        var doubling = Effective("proc_double");
        var tries = ModifierMath.ProcRolls(Effective("proc_reroll"), extraTries);

        _caps.TryGetValue(effectId, out var cap);
        return ChanceMath.RollTotal(perPerk, boost, doubling, tries, remainder => Rolls.Chance(effectId, remainder), cap);
    }

    /// <summary>
    /// Shared notes for the end of a turn, so two perks that both look at the same unused Vigor see the same amount and the
    /// Vigor is removed once, after both have acted.
    /// </summary>
    public sealed class TurnEndScratch
    {
        public int UnusedVigor;
        public bool ConsumeVigor;
    }

    /// <summary>Set for the duration of a turn-end Vigor conversion, so the "each Vigor consumed" perks don't fire on it (as in the old mod).</summary>
    public bool ConvertingVigor { get; internal set; }

    public TurnEndScratch TurnEnd { get; private set; } = new();

    // Every hook below is delivered for EVERY player's host on every client (lockstep), so each one first checks that
    // the event belongs to this host's owner. Skipping that check would apply player A's perks to player B.

    public override async Task BeforeCombatStart()
    {
        Rolls.BeginCombat(); // new combat, new streams: runs for every host on every client, active effects or not
        // Reset all instances, not just active ones, so a handler that becomes active later never starts with old state.
        foreach (var handler in _handlers.Values) handler.ResetCombat();
        foreach (var handler in Active) await handler.OnBeforeCombat();
    }

    public override async Task AfterRoomEntered(AbstractRoom room)
    {
        _votedThisMap = false; // a new map cycle begins once the party has moved on
        await ReconcilePotionSlots();
        await ReconcileMaxHp();
        foreach (var handler in Active) await handler.OnRoomEntered(room);

        // The game's documented place for "start of combat" powers: before the first turn, with a CombatRoom check.
        if (room is not CombatRoom) return;
        var ctx = new ThrowingPlayerChoiceContext(); // no player can be asked anything here
        foreach (var handler in Active) await handler.OnCombatRoomEntered(ctx);
        foreach (var handler in Active) await handler.OnOrbsChanged(ctx);
    }

    /// <summary>
    /// Potion+ : keeps the owner's potion slots equal to base + committed Potion+ total. Run on every room entry, so slots
    /// from a freshly committed perk arrive as soon as the player moves on, and nothing here fires mid-combat.
    ///
    /// The slots we added are counted in "potion_slot.granted" (saved with the rest of the perk state), so this only ever
    /// adds or removes the DIFFERENCE and never touches slots from Potion Belt or anything else. It is deliberately not a
    /// handler: a refunded perk has no active handler, and the extra slot must still go away.
    ///
    /// Taking a slot away is the dangerous direction: the game's SetMaxPotionCountInternal shuffles potions into free slots
    /// and discards whatever doesn't fit. So we only shrink by as many slots as are EMPTY right now, and finish the job on
    /// a later room once the player has used or discarded a potion. We never delete a potion.
    /// </summary>
    private async Task ReconcilePotionSlots()
    {
        const string Key = "potion_slot.granted";
        var want = (long)Math.Floor(Effective("potion_slot"));
        var have = State.Counters.Get(Key);
        var delta = want - have;
        if (delta > 0)
        {
            await PlayerCmd.GainMaxPotionCount((int)delta, Owner);
            State.Counters.Add(Key, delta);
        }
        else if (delta < 0)
        {
            var empty = Owner.MaxPotionCount - Owner.Potions.Count();
            var shrink = Math.Min(-delta, Math.Max(0, empty));
            if (shrink <= 0) return;
            await PlayerCmd.LoseMaxPotionCount((int)shrink, Owner);
            State.Counters.Add(Key, -shrink);
        }
    }

    /// <summary>
    /// Healthy: keeps the owner's Max HP equal to base + the COMMITTED Healthy total, adjusted on each room entry like Potion+.
    ///
    /// Only committed perks count (Effective reads the committed set), so allocating and refunding pending points on the map
    /// does nothing; the Max HP arrives once, after the perks are locked in, and cannot be farmed for healing. What was granted
    /// is kept in the saved counter "max_hp.granted", so only the difference is ever applied.
    ///
    /// Gaining uses the game's own GainMaxHp, which also heals by the amount, exactly like Strawberry. Losing (only if the
    /// committed total ever drops) never takes the last point.
    /// </summary>
    private async Task ReconcileMaxHp()
    {
        const string Key = "max_hp.granted";
        var me = Owner.Creature;
        if (me == null || !me.IsAlive) return;
        var want = (long)Math.Floor(Effective("max_hp"));
        var delta = want - State.Counters.Get(Key);
        if (delta > 0)
        {
            await CreatureCmd.GainMaxHp(me, delta);
            State.Counters.Add(Key, delta);
        }
        else if (delta < 0)
        {
            var lose = Math.Min(-delta, (long)me.MaxHp - 1);
            if (lose <= 0) return;
            await CreatureCmd.LoseMaxHp(new ThrowingPlayerChoiceContext(), me, lose, false);
            State.Counters.Add(Key, -lose);
        }
    }

    public override async Task AfterPlayerTurnStartLate(PlayerChoiceContext ctx, Player player)
    {
        if (!ReferenceEquals(player, Owner)) return;
        var turn = player.PlayerCombatState?.TurnNumber ?? 0;
        foreach (var handler in Active) await handler.OnTurnStart(ctx, turn);
        foreach (var handler in Active) await handler.OnOrbsChanged(ctx);   // slots can change between turns (Capacity, Preheated...)
    }

    public override async Task AfterCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        // Natives guard on CombatManager.IsInProgress: the card that kills the last enemy still reports as played.
        if (!ReferenceEquals(play.Card.Owner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnCardPlayed(ctx, play);
    }

    public override async Task AfterDamageReceived(
        PlayerChoiceContext ctx, Creature target, DamageResult result, ValueProp props, Creature? dealer, CardModel? cardSource)
    {
        if (!ReferenceEquals(target, Owner.Creature) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active)
        {
            await handler.OnDamaged(ctx, result);
            await handler.OnHitBy(ctx, result, props, dealer);
        }
    }

    public override async Task BeforeSideTurnEnd(PlayerChoiceContext ctx, CombatSide side, IEnumerable<Creature> creatures)
    {
        if (side != CombatSide.Player || !Owner.Creature.IsAlive || !creatures.Contains(Owner.Creature)) return;

        TurnEnd.UnusedVigor = (int)Owner.Creature.GetPowerAmount<VigorPower>();
        TurnEnd.ConsumeVigor = false;
        foreach (var handler in Active) await handler.OnTurnEnding(ctx);

        // Perks that spend unused Vigor at turn end only mark it; it is removed once, here, so they all saw the same amount.
        if (TurnEnd.ConsumeVigor && Owner.Creature.GetPower<VigorPower>() is { } vigor)
        {
            ConvertingVigor = true;
            try { await PowerCmd.Remove(vigor); }
            finally { ConvertingVigor = false; }
        }
    }

    public override async Task BeforeSideTurnStart(
        PlayerChoiceContext ctx, CombatSide side, IReadOnlyList<Creature> participants, ICombatState combatState)
    {
        // Like Bag of Marbles: only a turn the owner actually takes (in multiplayer an extra turn may leave other players out).
        if (side != CombatSide.Player || !participants.Contains(Owner.Creature)) return;
        var turn = Owner.PlayerCombatState?.TurnNumber ?? 0;
        foreach (var handler in Active) await handler.OnBeforeTurnStart(ctx, turn);
    }

    public override decimal ModifyHandDraw(Player player, decimal count)
    {
        if (!ReferenceEquals(player, Owner)) return count;
        var turn = player.PlayerCombatState?.TurnNumber ?? 0;
        foreach (var handler in Active) count = handler.ModifyHandDraw(count, turn);
        return count;
    }

    public override decimal ModifyRestSiteHealAmount(Creature creature, decimal amount)
    {
        if (!ReferenceEquals(creature, Owner.Creature)) return amount;
        foreach (var handler in Active) amount = handler.ModifyRestHeal(amount);
        return amount;
    }

    public override async Task AfterDamageGiven(
        PlayerChoiceContext ctx, Creature? dealer, DamageResult result, ValueProp props, Creature target, CardModel? cardSource)
    {
        if (!ReferenceEquals(dealer, Owner.Creature) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnDamageGiven(ctx, result, target);
    }

    public override CardLocation ModifyCardPlayResultLocation(CardModel card, bool isAutoPlay, ResourceInfo resources, CardLocation cardLocation)
    {
        if (!ReferenceEquals(card.Owner, Owner)) return cardLocation;
        foreach (var handler in Active) cardLocation = handler.ModifyCardResult(card, isAutoPlay, resources, cardLocation);
        return cardLocation;
    }

    // Was combat still running when the owner started using this potion? A potion that kills the last enemy ends the fight
    // before AfterPotionUsed runs, and Recycler should still refund it. Set in BeforePotionUsed, read in AfterPotionUsed.
    private bool _potionStartedInCombat;

    public override Task BeforePotionUsed(PotionModel potion, Creature? target)
    {
        if (ReferenceEquals(potion.Owner, Owner)) _potionStartedInCombat = CombatManager.Instance.IsInProgress;
        return Task.CompletedTask;
    }

    public override async Task AfterPotionUsed(PotionModel potion, Creature? target)
    {
        if (!ReferenceEquals(potion.Owner, Owner)) return;
        var startedInCombat = _potionStartedInCombat;
        _potionStartedInCombat = false;

        if (CombatManager.Instance.IsInProgress)
        {
            foreach (var handler in Active) await handler.OnPotionUsed(potion, target);
        }
        else if (startedInCombat)
        {
            // This potion ended the fight. Block, Strength and the rest would be wasted, so only perks that ask for it run.
            foreach (var handler in Active)
                if (handler.RunsWhenPotionEndsCombat) await handler.OnPotionUsed(potion, target);
        }
        // Anything else (a potion used on the map, at a merchant, in an event) is not a combat use and gets no perks.
    }

    public override async Task AfterPowerAmountChanged(
        PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature? applier, CardModel? cardSource)
    {
        var holder = power.Owner;
        if (ReferenceEquals(holder, Owner.Creature))
        {
            foreach (var handler in Active) await handler.OnPowerChanged(ctx, power, amount);
        }
        else if (holder is { Side: CombatSide.Enemy } && ReferenceEquals(applier, Owner.Creature))
        {
            // A power the owner put on an enemy. Bonus copies are applied with the enemy as its own applier, so they land in neither branch.
            foreach (var handler in Active) await handler.OnPowerAppliedToEnemy(ctx, power, amount, holder);
        }
    }

    public override bool TryModifyPowerAmountReceived(
        PowerModel canonicalPower, Creature target, decimal amount, Creature? applier, out decimal modifiedAmount)
    {
        modifiedAmount = amount;
        if (!ReferenceEquals(target, Owner.Creature)) return false;
        foreach (var handler in Active)
        {
            var changed = handler.ModifyPowerReceived(canonicalPower, applier, amount);
            if (changed == amount) continue;
            modifiedAmount = changed;
            return true;
        }
        return false;
    }

    public override async Task AfterCardExhausted(PlayerChoiceContext ctx, CardModel card, bool causedByEthereal)
    {
        if (!ReferenceEquals(card.Owner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnCardExhausted(ctx, card, causedByEthereal);
    }

    public override async Task AfterCardDiscarded(PlayerChoiceContext ctx, CardModel card)
    {
        if (!ReferenceEquals(card.Owner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnCardDiscarded(ctx, card);
    }

    public override async Task AfterCardDrawn(PlayerChoiceContext ctx, CardModel card, bool fromHandDraw)
    {
        if (!ReferenceEquals(card.Owner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnCardDrawn(ctx, card, fromHandDraw);
    }

    public override async Task BeforeFlushLate(PlayerChoiceContext ctx, Player player)
    {
        if (!ReferenceEquals(player, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnBeforeFlush(ctx);
    }

    /// <summary>The active handler of a given type, or null. For the few effects that run from a Harmony patch instead of a hook.</summary>
    public T? Handler<T>() where T : EffectHandler
    {
        foreach (var handler in Active)
            if (handler is T match) return match;
        return null;
    }

    public override async Task AfterCurrentHpChanged(Creature creature, decimal delta)
    {
        if (!CombatManager.Instance.IsInProgress) return;
        if (ReferenceEquals(creature, Owner.Creature))
            foreach (var handler in Active) await handler.OnHpChanged(delta);
        else if (Owner.Osty is { } osty && ReferenceEquals(creature, osty))
            foreach (var handler in Active) await handler.OnOstyHpChanged(delta);
    }

    // ---- orbs, summons, forging and Stars: one hook each, filtered to the owner like every other hook ---------------

    public override async Task AfterOrbChanneled(PlayerChoiceContext ctx, Player player, OrbModel orb)
    {
        if (!ReferenceEquals(player, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnOrbChanneled(ctx, orb);
        foreach (var handler in Active) await handler.OnOrbsChanged(ctx);
    }

    public override async Task AfterOrbEvoked(PlayerChoiceContext ctx, OrbModel orb, IEnumerable<Creature> targets)
    {
        if (!ReferenceEquals(orb.Owner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnOrbsChanged(ctx);
    }

    public override async Task AfterSummon(PlayerChoiceContext ctx, Player summoner, decimal amount)
    {
        if (!ReferenceEquals(summoner, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnSummon(ctx, amount);
    }

    public override async Task AfterForge(decimal amount, Player forger, AbstractModel source)
    {
        if (!ReferenceEquals(forger, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnForge(amount);
    }

    public override async Task AfterStarsGained(int amount, Player gainer)
    {
        if (!ReferenceEquals(gainer, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnStarsGained(amount);
    }

    public override async Task AfterStarsSpent(int amount, Player spender)
    {
        if (!ReferenceEquals(spender, Owner) || !CombatManager.Instance.IsInProgress) return;
        foreach (var handler in Active) await handler.OnStarsSpent(amount);
    }

    // HOT PATH. The game calls this whenever it works out damage, including card previews, for every model. For a player with
    // no damage perk it is one int comparison and an empty-array check; otherwise a reference check and a few comparisons.
    public override decimal ModifyDamageMultiplicative(
        Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (_activeVersion != State.CommittedVersion) _ = Active; // refresh the cached lists after a commit
        var mods = _damageMods;
        if (mods.Length == 0 || target is null || !ReferenceEquals(dealer, Owner.Creature)) return 1m;
        var multiplier = 1m;
        foreach (var handler in mods) multiplier *= handler.DamageMultiplier(target, props);
        return multiplier;
    }

    // The rewards screen after a fight. This is the game's own way to add a reward (Amethyst Aubergine adds a gold reward the same way):
    // the new entries are separate buttons next to the usual gold, card and potion rewards, not folded into them.
    public override bool TryModifyRewards(Player player, List<Reward> rewards, AbstractRoom room)
    {
        if (!ReferenceEquals(player, Owner) || room is not CombatRoom) return false;
        var changed = false;
        foreach (var handler in Active) changed |= handler.ModifyRewards(rewards);
        return changed;
    }

    // HOT PATH, like ModifyDamageMultiplicative: previews ask too. One comparison and an empty-array check for a player without such a perk.
    public override decimal ModifyDamageAdditive(
        Creature? target, decimal amount, ValueProp props, Creature? dealer, CardModel? cardSource, CardPlay? cardPlay)
    {
        if (_activeVersion != State.CommittedVersion) _ = Active;
        var mods = _incomingMods;
        if (mods.Length == 0 || target is null || !ReferenceEquals(target, Owner.Creature)) return 0m;
        var adjust = 0m;
        foreach (var handler in mods) adjust += handler.IncomingDamageAdjust(dealer, props);
        return adjust;
    }

    public override Task AfterCombatVictory(CombatRoom room)
    {
        if (ReferenceEquals(room, _lastAwardedRoom)) return Task.CompletedTask;
        _lastAwardedRoom = room;

        var kind = room.RoomType switch
        {
            MegaCrit.Sts2.Core.Rooms.RoomType.Elite => RoomKind.Elite,
            MegaCrit.Sts2.Core.Rooms.RoomType.Boss => RoomKind.Boss,
            _ => RoomKind.Normal,
        };
        State.AwardForCombat(kind);
        MainFile.Log($"{Owner.NetId}: +{PointRules.ForCombat(kind)} perk points ({State.UnspentPoints} unspent)");
        return Task.CompletedTask;
    }
}
