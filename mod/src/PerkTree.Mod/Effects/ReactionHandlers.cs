using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Effects that react to a power being applied. Two ways a bonus could trigger itself, and both are closed:
//  - the bonus names the ENEMY as its own applier, so it doesn't look player-applied (the old mod's trick, and our host only
//    reacts to powers the OWNER applied), and
//  - a re-entrancy flag, because chance effects can now exceed 100% and a certain proc that retriggered would never stop.

/// <summary>"When you apply X to an enemy, a chance to apply more": each perk rolls on its own and gives its amount.</summary>
internal abstract class ExtraApplicationChance<T> : EffectHandler where T : PowerModel
{
    private bool _applying;
    public override void ResetCombat() => _applying = false;

    public override async Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target)
    {
        if (_applying || power is not T || amount <= 0) return;
        var extra = Rolled();
        if (extra <= 0) return;
        _applying = true;
        try { await PowerCmd.Apply<T>(ctx, target, extra, target, null, false); }
        finally { _applying = false; }
    }
}

internal sealed class VulnerableExtraChance : ExtraApplicationChance<VulnerablePower> { public override string Id => "vulnerable_extra_chance"; }
internal sealed class PoisonExtraChance : ExtraApplicationChance<PoisonPower> { public override string Id => "poison_extra_chance"; }

/// <summary>Dooming (Necrobinder): when you apply Doom to an enemy, apply more. A flat amount per perk, not a chance.</summary>
internal sealed class DoomExtra : EffectHandler
{
    private bool _applying;
    public override string Id => "doom_extra";
    public override void ResetCombat() => _applying = false;

    public override async Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target)
    {
        if (_applying || power is not DoomPower || amount <= 0) return;
        _applying = true;
        try { await PowerCmd.Apply<DoomPower>(ctx, target, Rolled(), target, null, false); }
        finally { _applying = false; }
    }
}

/// <summary>Focused (Defect): the first time you gain Focus each combat, from any source, gain more. Once per combat, so its own bonus can't repeat it.</summary>
internal sealed class FirstFocusBonus : EffectHandler
{
    private bool _used;
    public override string Id => "first_focus_bonus";
    public override void ResetCombat() => _used = false;

    public override async Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount)
    {
        if (_used || power is not FocusPower || amount <= 0) return;
        _used = true;   // before the grant, so the grant's own power-change doesn't count as another first gain
        await GrantPower<FocusPower>(ctx, Rolled());
    }
}

/// <summary>
/// Obstinate: Strength or Dexterity an ENEMY would take from you is gained instead. Done by flipping the sign of the amount about to
/// land, in one step (no second application). Your own Strength drops (Flex-style) are not touched, so there is no give-then-take trick.
/// </summary>
internal sealed class LossInvert : EffectHandler
{
    public override string Id => "loss_invert";

    public override decimal ModifyPowerReceived(PowerModel power, Creature? applier, decimal amount)
        => amount < 0 && applier is { Side: CombatSide.Enemy } && power is StrengthPower or DexterityPower ? -amount : amount;
}

/// <summary>
/// Toxic Ward: each time YOU apply Poison to an enemy, a chance to gain Block. Counts every application, one per enemy, so Hidden
/// Dose over three enemies is three rolls. Poison put on by Envenomed's bonus copy does not count (it names the enemy as its own
/// applier, like every bonus copy). Gives Block, never Poison, so nothing here can trigger itself.
/// </summary>
internal sealed class PoisonApplyBlock : EffectHandler
{
    public override string Id => "poison_apply_block";
    public override Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target)
        => power is PoisonPower && amount > 0 ? GrantBlock(Rolled()) : Task.CompletedTask;
}

/// <summary>
/// Poison Cloud: each time YOU apply Poison to an enemy, a chance to hit THAT enemy for flat damage (Unpowered, like the other
/// perk damage, so Strength and Vulnerable don't scale it). Damage cannot apply Poison, so it cannot trigger itself.
/// </summary>
internal sealed class PoisonApplyDamage : EffectHandler
{
    public override string Id => "poison_apply_damage";
    public override async Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target)
    {
        if (power is not PoisonPower || amount <= 0 || !target.IsAlive) return;
        var damage = Rolled();
        if (damage > 0) await CreatureCmd.Damage(ctx, target, damage, Flat, Me);
    }
}
