using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.ValueProps;

namespace PerkTree.Mod.Effects;

/// <summary>
/// What one effect id does in combat. There is one instance per (player, effect), so a handler may keep its own
/// counters in plain fields: nothing is shared between players, which matters in co-op because every client runs
/// every player's handlers.
///
/// The host does the filtering before calling in: a handler only hears about ITS owner's turn, cards and damage,
/// only while a combat is in progress, and only while the effect's committed total is above zero. A handler that
/// only needs one hook overrides just that one.
///
/// STACKING: chance effects are not capped at 100%, so a stack can make a proc certain. A handler whose bonus triggers the very
/// hook it listens to (an "apply 1 more X" perk that applies X) MUST guard against re-triggering itself, as GainAmplifyBase
/// does, or a certain proc would loop forever. Infinite SCALING is fine; infinite LOOPS are not.
///
/// CO-OP RULES a handler must keep, because all clients simulate in lockstep:
///  - await every game command, in order, with the hook's own PlayerChoiceContext (never fire-and-forget);
///  - decide only from synced game state and its own counters (which are driven by synced events);
///  - randomness only through <see cref="Rolled"/> (the game's seeded RNG). No System.Random, Rng.Chaotic, clock, Guid,
///    string.GetHashCode or LocalContext: the outcome has to be the same on every machine. tools/check_handlers.py
///    fails the build check if any of those appear in an effect.
/// </summary>
internal abstract class EffectHandler
{
    public abstract string Id { get; }

    // Set by the host when it creates the handler.
    internal PerkHostModel Host { get; set; } = null!;

    protected Player Owner => Host.Owner;
    protected Creature Me => Host.Owner.Creature;

    /// <summary>The committed, cap-clamped total for this effect, as if every roll succeeded. For pure hooks and thresholds; a handler that ACTS on a trigger uses <see cref="Rolled"/>. Read it when you need it, don't cache it.</summary>
    protected decimal Amount => Host.Effective(Id);

    /// <summary>
    /// A run-long counter for this effect, saved with the run. Change it only from inside a hook: every client then
    /// counts the same synced events and holds the same value, so nothing needs sending. Keys are "effect_id.name".
    /// </summary>
    protected long Count(string name) => Host.State.Counters.Get(Id + "." + name);

    /// <summary>Adds to one of this effect's run-long counters and returns the new value.</summary>
    protected long CountUp(string name, long by = 1) => Host.State.Counters.Add(Id + "." + name, by);

    /// <summary>
    /// What this effect gives on THIS trigger: roll each perk's chance (see <see cref="PerkHostModel.Roll"/>) and add up the
    /// amounts of the perks that succeeded. Call it once per trigger, at the moment you act, and use the result as the amount
    /// ("grant Rolled() Block"). With no chances set it is just the plain sum, so it matches <see cref="Amount"/> and uses no RNG.
    /// Do NOT call it from a pure hook that the game may run many times (damage previews, hand-draw modifiers): those use Amount.
    /// </summary>
    protected decimal Rolled() => Host.Roll(Id, ExtraTries);

    /// <summary>Extra attempts on a failed roll, on top of proc_reroll. Only Frenzied's Vigor-spending perks use it (see <see cref="VigorSpentProc"/>).</summary>
    protected virtual int ExtraTries => 0;

    /// <summary>
    /// <see cref="Rolled"/> for something that happens <paramref name="times"/> times in one trigger (per unused Vigor, say): each
    /// occurrence rolls separately. Capped, so a big count can't mean thousands of rolls.
    /// </summary>
    protected decimal RolledEach(int times)
    {
        var total = 0m;
        for (var i = 0; i < Math.Min(times, MaxRolledEach); i++) total += Host.Roll(Id, ExtraTries);
        return total;
    }

    private const int MaxRolledEach = 50;

    /// <summary>A potion-use amount after potion_boost and potion_double. Zero stays zero.</summary>
    protected decimal PotionAmount(decimal baseAmount)
        => ModifierMath.PotionAmount(baseAmount, Host.Effective("potion_boost"), Host.Effective("potion_double"));

    // ---- hooks (each one maps to exactly one game hook; see PerkHostModel) -------------------------------------

    /// <summary>BeforeCombatStart. Clear PER-COMBAT in-memory state here (a once-per-combat flag, say). Run-long counts are NOT cleared: they live in <see cref="Count"/>.</summary>
    public virtual void ResetCombat() { }

    /// <summary>BeforeCombatStart, after ResetCombat. The slot native "start of combat" block (Anchor) uses.</summary>
    public virtual Task OnBeforeCombat() => Task.CompletedTask;

    /// <summary>AfterRoomEntered for a combat room. The slot native start-of-combat powers (Bronze Scales) use.</summary>
    public virtual Task OnCombatRoomEntered(PlayerChoiceContext ctx) => Task.CompletedTask;

    /// <summary>AfterPlayerTurnStartLate (after the hand is drawn, like Blood Vial). turn is the player's 1-based turn.</summary>
    public virtual Task OnTurnStart(PlayerChoiceContext ctx, int turn) => Task.CompletedTask;

    /// <summary>AfterCardPlayed, for cards the owner played.</summary>
    public virtual Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play) => Task.CompletedTask;

    /// <summary>AfterDamageReceived, when the owner's creature was the one hit.</summary>
    public virtual Task OnDamaged(PlayerChoiceContext ctx, DamageResult result) => Task.CompletedTask;

    /// <summary>BeforeSideTurnEnd for the player side, while the owner is alive.</summary>
    public virtual Task OnTurnEnding(PlayerChoiceContext ctx) => Task.CompletedTask;

    /// <summary>BeforeSideTurnStart for a turn the owner takes, before energy reset and draw (where Bag of Marbles and Red Mask debuff enemies). turn is 1-based, 0 or 1 on the first.</summary>
    public virtual Task OnBeforeTurnStart(PlayerChoiceContext ctx, int turn) => Task.CompletedTask;

    /// <summary>
    /// ModifyHandDraw: change how many cards the owner draws at turn start. Must be a pure function of synced state: it
    /// can also run for previews, so no counters, no rolls, no commands.
    /// </summary>
    public virtual decimal ModifyHandDraw(decimal count, int turn) => count;

    /// <summary>AfterDamageGiven, when the OWNER dealt the damage (to an enemy, or anything else the game reports).</summary>
    public virtual Task OnDamageGiven(PlayerChoiceContext ctx, DamageResult result, Creature target) => Task.CompletedTask;

    /// <summary>AfterPotionUsed, for a potion the owner used during combat. Potion hooks carry no choice context.</summary>
    public virtual Task OnPotionUsed(PotionModel potion, Creature? target) => Task.CompletedTask;

    /// <summary>
    /// Where a card the owner is playing ends up (ModifyCardPlayResultLocation). Asked ONCE per play, when the play starts (never for previews),
    /// in the same order on every client, so a handler may roll here. Return the location unchanged to leave it alone.
    /// </summary>
    public virtual CardLocation ModifyCardResult(CardModel card, bool isAutoPlay, ResourceInfo resources, CardLocation location) => location;

    /// <summary>True for a perk that must also see the potion that ended the fight (it killed the last enemy). Only Recycler: it is about the potion, not the fight.</summary>
    public virtual bool RunsWhenPotionEndsCombat => false;

    /// <summary>AfterPowerAmountChanged for a power ON the owner. amount is the change (negative when it went down, as when Vigor is spent).</summary>
    public virtual Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount) => Task.CompletedTask;

    /// <summary>AfterCurrentHpChanged for the owner. delta is negative for damage or HP loss, positive for healing.</summary>
    public virtual Task OnHpChanged(decimal delta) => Task.CompletedTask;

    /// <summary>AfterCardExhausted, for a card the owner owns. causedByEthereal is true for the end-of-turn Ethereal exhaust.</summary>
    public virtual Task OnCardExhausted(PlayerChoiceContext ctx, CardModel card, bool causedByEthereal) => Task.CompletedTask;

    /// <summary>AfterCardDiscarded, for a card the owner owns (a normal discard, a forced one, or an exhaust the owner dodged).</summary>
    public virtual Task OnCardDiscarded(PlayerChoiceContext ctx, CardModel card) => Task.CompletedTask;

    /// <summary>AfterCardDrawn, for a card the owner drew. This runs inside the draw loop, so anything done here is awaited in order.</summary>
    public virtual Task OnCardDrawn(PlayerChoiceContext ctx, CardModel card, bool fromHandDraw) => Task.CompletedTask;

    /// <summary>BeforeFlushLate for the owner: the end of their turn, after the game's own retain effects have marked cards.</summary>
    public virtual Task OnBeforeFlush(PlayerChoiceContext ctx) => Task.CompletedTask;

    /// <summary>
    /// AfterPowerAmountChanged for a power on an ENEMY that the owner applied (poison, doom, vulnerable...). Bonus applications
    /// should name the enemy itself as the applier, which is how the old mod kept a bonus from reading as a fresh player-applied
    /// power and triggering itself again; a re-entrancy flag on top is cheap insurance.
    /// </summary>
    public virtual Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target) => Task.CompletedTask;

    /// <summary>
    /// TryModifyPowerAmountReceived for a power about to land on the OWNER. Return the amount unchanged to leave it alone.
    /// Must be pure: it can be asked more than once and for previews.
    /// </summary>
    public virtual decimal ModifyPowerReceived(PowerModel power, Creature? applier, decimal amount) => amount;

    /// <summary>AfterOrbChanneled, for an orb the owner channeled. A bonus Channel from here fires this again, so guard against looping.</summary>
    public virtual Task OnOrbChanneled(PlayerChoiceContext ctx, OrbModel orb) => Task.CompletedTask;

    /// <summary>The owner's orb slots may have just changed (an orb was channeled or evoked, a turn started, combat began): look at them again.</summary>
    public virtual Task OnOrbsChanged(PlayerChoiceContext ctx) => Task.CompletedTask;

    /// <summary>AfterSummon, when the owner summoned (Osty). amount is how much was summoned.</summary>
    public virtual Task OnSummon(PlayerChoiceContext ctx, decimal amount) => Task.CompletedTask;

    /// <summary>AfterForge, when the owner forged. This hook carries no choice context.</summary>
    public virtual Task OnForge(decimal amount) => Task.CompletedTask;

    /// <summary>AfterStarsGained for the owner. Gaining Stars from here fires this again, so guard against looping.</summary>
    public virtual Task OnStarsGained(int amount) => Task.CompletedTask;

    /// <summary>AfterStarsSpent for the owner.</summary>
    public virtual Task OnStarsSpent(int amount) => Task.CompletedTask;

    /// <summary>AfterCurrentHpChanged for the owner's Osty. delta is negative when it lost HP.</summary>
    public virtual Task OnOstyHpChanged(decimal delta) => Task.CompletedTask;

    /// <summary>AfterDamageReceived for the owner, with who dealt it and how. OnDamaged only gets the result.</summary>
    public virtual Task OnHitBy(PlayerChoiceContext ctx, DamageResult result, ValueProp props, Creature? dealer) => Task.CompletedTask;

    /// <summary>
    /// TryModifyRewards for the rewards screen after a COMBAT the owner fought: add your own entries to <paramref name="rewards"/> and return
    /// true if you changed the list. Every client builds the same list for every player, so anything random must come from <see cref="Rolled"/>.
    /// </summary>
    public virtual bool ModifyRewards(List<Reward> rewards) => false;

    /// <summary>True if <see cref="IncomingDamageAdjust"/> does anything. Lets the host skip the hook entirely for most players.</summary>
    public virtual bool ModifiesIncomingDamage => false;

    /// <summary>
    /// ModifyDamageAdditive for damage dealt TO the owner: a number added to it (negative reduces). HOT PATH like
    /// <see cref="DamageMultiplier"/>: previews ask too, so a few comparisons, no rolls, no commands, no state.
    /// </summary>
    public virtual decimal IncomingDamageAdjust(Creature? dealer, ValueProp props) => 0m;

    /// <summary>True if <see cref="DamageMultiplier"/> does anything. Lets the host skip the damage hook entirely for most players.</summary>
    public virtual bool ModifiesDamage => false;

    /// <summary>
    /// ModifyDamageMultiplicative for damage the owner is about to deal. HOT PATH: the game also asks while previewing
    /// card damage, so this must be a few comparisons, with no allocation, no rolls and no commands.
    /// </summary>
    public virtual decimal DamageMultiplier(Creature target, ValueProp props) => 1m;

    // ---- shared helpers, written the way the native relics call the same commands ------------------------------

    // Flat amounts that aren't a card's own effect are Unpowered (that's the flag the native relics pass for Anchor,
    // Orichalcum, Mercury Hourglass...): Strength/Dexterity don't scale a perk's flat number.
    protected const ValueProp Flat = ValueProp.Unpowered;

    protected Task GrantPower<T>(PlayerChoiceContext ctx, decimal amount) where T : PowerModel
        => amount > 0
            ? PowerCmd.Apply<T>(ctx, Me, amount, Me, null, false)
            : Task.CompletedTask;

    protected async Task GrantBlock(decimal amount)
    {
        if (amount > 0) await CreatureCmd.GainBlock(Me, amount, Flat, null, false);
    }

    protected async Task GrantEnergy(decimal amount)
    {
        if (amount > 0) await PlayerCmd.GainEnergy(amount, Owner);
    }

    protected async Task Draw(PlayerChoiceContext ctx, decimal count)
    {
        if (count > 0) await CardPileCmd.Draw(ctx, count, Owner, false);
    }

    /// <summary>The enemies the owner can currently hit, copied (applying powers or damage can change the live list).</summary>
    protected List<Creature> Enemies()
        => Me.CombatState?.HittableEnemies.ToList() ?? new List<Creature>();

    /// <summary>One random living enemy, picked with the game's seeded RNG (null if there is none).</summary>
    protected Creature? PickEnemy()
    {
        var enemies = Enemies();
        return enemies.Count == 0 ? null : enemies[Host.Rolls.Next(Id, enemies.Count)];
    }

    /// <summary>Applies a power to every enemy the owner can hit.</summary>
    protected async Task GrantPowerToEnemies<T>(PlayerChoiceContext ctx, decimal amount) where T : PowerModel
    {
        if (amount <= 0) return;
        var targets = Enemies();
        if (targets.Count > 0) await PowerCmd.Apply<T>(ctx, targets, amount, Me, null, false);
    }

    /// <summary>Channels one random Orb for the owner, picked by the game's own random-orb function from this effect's seeded stream.</summary>
    protected Task ChannelRandomOrb(PlayerChoiceContext ctx)
        => OrbCmd.Channel(ctx, OrbModel.GetRandomOrb(Host.Rolls.StreamOf(Id)), Owner);

    /// <summary>A non-interactive context for hooks that don't hand us one (the game does the same for relics that apply powers from them).</summary>
    protected static PlayerChoiceContext NoChoice() => new ThrowingPlayerChoiceContext();

    /// <summary>Flat damage to every enemy the owner can hit. The list is copied first because damage can kill and reshuffle it.</summary>
    protected async Task DamageAllEnemies(PlayerChoiceContext ctx, decimal amount)
    {
        if (amount <= 0) return;
        var targets = Me.CombatState?.HittableEnemies;
        if (targets == null) return;
        var snapshot = targets.ToList();
        if (snapshot.Count == 0) return;
        await CreatureCmd.Damage(ctx, snapshot, amount, Flat, Me);
    }
}
