using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// The "companion" effects: each one mirrors a native relic's idiom (named in the comment) so it fires from the same
// hook and with the same command shape the game itself uses. The thresholds (every 4th turn, per 12 attacks...) are
// constants here because they are the effect's definition; the per-node AMOUNT comes from the tree.

// ---- combat start ---------------------------------------------------------------------------------------------

/// <summary>Thorns at combat start. Idiom: Bronze Scales (AfterRoomEntered).</summary>
internal sealed class StartThorns : CombatStartPower<ThornsPower>
{
    public override string Id => "start_thorns";
}

/// <summary>Block at combat start. Idiom: Anchor (BeforeCombatStart, so it isn't swept away by the turn-1 block reset).</summary>
internal sealed class StartBlock : EffectHandler
{
    public override string Id => "start_block";
    public override Task OnBeforeCombat() => GrantBlock(Rolled());
}

/// <summary>Vigor on turn 1. Idiom: Akabeko (turn 1 of the owner).</summary>
internal sealed class StartVigor : EffectHandler
{
    public override string Id => "start_vigor";
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn)
        => turn == 1 ? GrantPower<VigorPower>(ctx, Rolled()) : Task.CompletedTask;
}

/// <summary>Heal on turn 1. Idiom: Blood Vial (AfterPlayerTurnStartLate, turn 1, Heal(creature, amount, true)).</summary>
internal sealed class StartHeal : EffectHandler
{
    public override string Id => "start_heal";
    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        if (turn != 1) return;
        var heal = Rolled();
        if (heal > 0) await CreatureCmd.Heal(Me, heal, true);
    }
}

// ---- every turn ------------------------------------------------------------------------------------------------

/// <summary>Damage to all enemies at each turn start. Idiom: Mercury Hourglass.</summary>
internal sealed class TurnPingDamage : EffectHandler
{
    public override string Id => "turn_ping_damage";
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) => DamageAllEnemies(ctx, Rolled());
}

/// <summary>
/// Energy on every Nth turn, counted across the whole run so short fights still get there. Idiom: Happy Flower (counts
/// turns, fires on the Nth), except the count is saved with the run instead of restarting each combat.
/// </summary>
internal sealed class EnergyEveryNTurns : EffectHandler
{
    // Every 4th turn, matching the old "Surging" prefix.
    private const int Interval = 4;

    public override string Id => "energy_every_n_turns";
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn)
        => CountUp("turns") % Interval == 0 ? GrantEnergy(Rolled()) : Task.CompletedTask;
}

/// <summary>Block at end of turn if you have none. Idiom: Orichalcum (BeforeSideTurnEnd).</summary>
internal sealed class EndTurnNoBlock : EffectHandler
{
    public override string Id => "end_turn_no_block";
    public override Task OnTurnEnding(PlayerChoiceContext ctx)
        => Me.Block == 0 ? GrantBlock(Rolled()) : Task.CompletedTask;
}

// ---- on card played --------------------------------------------------------------------------------------------

/// <summary>Energy per N attacks played, counted across the whole run (saved with it), like Nunchaku.</summary>
internal sealed class EnergyPerAttacks : EffectHandler
{
    // One Energy per 12 attacks, matching the old "Accelerating" prefix.
    private const int Threshold = 12;

    public override string Id => "energy_per_attacks";

    public override Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
        => play.Card.Type == CardType.Attack && CountUp("attacks") % Threshold == 0
            ? GrantEnergy(Rolled())
            : Task.CompletedTask;
}

/// <summary>
/// Base for "every N cards of a type played in one turn". The count resets each turn, and fires on each multiple
/// (4th, 8th, 12th...), not only once.
/// </summary>
internal abstract class PerTurnCardCounter : EffectHandler
{
    protected abstract CardType Counted { get; }
    protected abstract int Threshold { get; }
    protected abstract Task Fire(PlayerChoiceContext ctx);

    // Per turn, in memory only: it restarts every turn, so there is nothing worth saving (the game never saves mid-turn).
    private int _count;
    public override void ResetCombat() => _count = 0;

    // The turn-start hook is the reset point. It runs after the hand is drawn but before the player can act.
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        _count = 0;
        return Task.CompletedTask;
    }

    public override Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (play.Card.Type != Counted) return Task.CompletedTask;
        return ++_count % Threshold == 0 ? Fire(ctx) : Task.CompletedTask;
    }
}

internal sealed class AttackStrength : PerTurnCardCounter
{
    public override string Id => "attack_strength";
    protected override CardType Counted => CardType.Attack;
    protected override int Threshold => 4;
    protected override Task Fire(PlayerChoiceContext ctx) => GrantPower<StrengthPower>(ctx, Rolled());
}

internal sealed class AttackDexterity : PerTurnCardCounter
{
    public override string Id => "attack_dexterity";
    protected override CardType Counted => CardType.Attack;
    protected override int Threshold => 4;
    protected override Task Fire(PlayerChoiceContext ctx) => GrantPower<DexterityPower>(ctx, Rolled());
}

internal sealed class AttackBlock : PerTurnCardCounter
{
    public override string Id => "attack_block";
    protected override CardType Counted => CardType.Attack;
    protected override int Threshold => 4;
    protected override Task Fire(PlayerChoiceContext ctx) => GrantBlock(Rolled());
}

internal sealed class SkillDamageAll : PerTurnCardCounter
{
    public override string Id => "skill_damage_all";
    protected override CardType Counted => CardType.Skill;
    protected override int Threshold => 4;
    protected override Task Fire(PlayerChoiceContext ctx) => DamageAllEnemies(ctx, Rolled());
}

// ---- on damage taken -------------------------------------------------------------------------------------------

/// <summary>
/// Draw cards the first time you take HP damage each combat. Idiom: Centennial Puzzle. "First" is only used up by a success: with a
/// chance below 100% a failed roll lets the next unblocked hit try again, until one succeeds.
/// </summary>
internal sealed class DrawOnFirstHit : EffectHandler
{
    private bool _used;

    public override string Id => "draw_on_first_hit";
    public override void ResetCombat() => _used = false;

    public override async Task OnDamaged(PlayerChoiceContext ctx, DamageResult result)
    {
        // Puzzle only counts damage that got through block.
        if (_used || result.UnblockedDamage <= 0) return;
        var cards = Rolled();
        if (cards <= 0) return;   // failed roll: not used up
        _used = true;
        await Draw(ctx, cards);
    }
}
