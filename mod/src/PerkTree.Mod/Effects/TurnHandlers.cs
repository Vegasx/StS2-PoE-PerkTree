using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace PerkTree.Mod.Effects;

// Per-turn grants, draw changes, "first time this combat" effects and the execute bonus.

/// <summary>Strength every turn from turn 5 on.</summary>
internal sealed class EnduringStrength : EffectHandler
{
    private const int FromTurn = 5;
    public override string Id => "enduring_strength";
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn)
        => turn >= FromTurn ? GrantPower<StrengthPower>(ctx, Rolled()) : Task.CompletedTask;
}

/// <summary>Vigor every turn.</summary>
internal sealed class TurnVigor : EffectHandler
{
    public override string Id => "turn_vigor";
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) => GrantPower<VigorPower>(ctx, Rolled());
}

/// <summary>
/// Extra cards on the first turn. Drawn right after the opening hand, not folded into it through ModifyHandDraw (the way Pocketwatch does
/// it), because that hook is also asked for previews and so can't roll a chance. The hand you end up with is the same.
/// </summary>
internal sealed class FirstTurnDraw : EffectHandler
{
    public override string Id => "first_turn_draw";

    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) => turn == 1 ? Draw(ctx, Rolled()) : Task.CompletedTask;
}

/// <summary>Extra cards each turn while at or below half HP.</summary>
internal sealed class LowHpDraw : EffectHandler
{
    public override string Id => "low_hp_draw";
    public override decimal ModifyHandDraw(decimal count, int turn)
        => Me.CurrentHp * 2 <= Me.MaxHp ? count + Amount : count;
}

/// <summary>Block the first time you drop to half HP in a combat.</summary>
internal sealed class SecondWindBlock : EffectHandler
{
    private bool _used;
    public override string Id => "second_wind_block";
    public override void ResetCombat() => _used = false;

    public override async Task OnHpChanged(decimal delta)
    {
        // Only a drop counts, and not the hit that kills.
        if (_used || delta >= 0 || Me.CurrentHp <= 0 || Me.CurrentHp * 2 > Me.MaxHp) return;
        _used = true;
        await GrantBlock(Rolled());
    }
}

/// <summary>Strength the first time you lose HP in a combat (Ironclad).</summary>
internal sealed class FirstHpLossStrength : EffectHandler
{
    private bool _used;
    public override string Id => "first_hp_loss_strength";
    public override void ResetCombat() => _used = false;

    public override async Task OnHpChanged(decimal delta)
    {
        if (_used || delta >= 0) return;
        _used = true;
        await GrantPower<StrengthPower>(NoChoice(), Rolled());
    }
}

/// <summary>Cards drawn the first time you play a Power each combat.</summary>
internal sealed class PowerFirstDraw : EffectHandler
{
    private bool _used;
    public override string Id => "power_first_draw";
    public override void ResetCombat() => _used = false;

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (_used || play.Card.Type != CardType.Power) return;
        _used = true;
        await Draw(ctx, Rolled());
    }
}

/// <summary>Energy when you play a Power, once per turn.</summary>
internal sealed class PowerEnergy : EffectHandler
{
    private bool _usedThisTurn;
    public override string Id => "power_energy";
    public override void ResetCombat() => _usedThisTurn = false;
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) { _usedThisTurn = false; return Task.CompletedTask; }

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (_usedThisTurn || play.Card.Type != CardType.Power) return;
        _usedThisTurn = true;
        await GrantEnergy(Rolled());
    }
}

/// <summary>At turn start, Vigor equal to half the HP lost since the last turn (Ironclad). Counts every HP drop, not the net change, so healing doesn't hide it.</summary>
internal sealed class HpLostVigor : EffectHandler
{
    private decimal _lostSinceTurn;
    public override string Id => "hp_lost_vigor";
    public override void ResetCombat() => _lostSinceTurn = 0;

    public override Task OnHpChanged(decimal delta)
    {
        if (delta < 0) _lostSinceTurn += -delta;
        return Task.CompletedTask;
    }

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var halves = Math.Floor(_lostSinceTurn * 0.5m);
        _lostSinceTurn = 0;
        if (halves > 0) await GrantPower<VigorPower>(ctx, halves * Rolled());   // roll only when there is something to gain
    }
}

/// <summary>More damage to enemies at or below 20% HP. HOT PATH: runs for every damage calculation the owner makes, previews included.</summary>
internal sealed class ExecuteBonus : EffectHandler
{
    public override string Id => "execute_bonus";
    public override bool ModifiesDamage => true;

    public override decimal DamageMultiplier(Creature target, ValueProp props)
    {
        // Skip the perks' own flat damage (Unpowered) so it can't snowball; only card and attack damage scales.
        if ((props & ValueProp.Unpowered) != 0) return 1m;
        return target.IsEnemy && target.CurrentHp * 5 <= target.MaxHp ? 1m + Amount : 1m;
    }
}
