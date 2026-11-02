using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Gambles that put debuffs on enemies AND on the owner, and the one that moves debuffs between enemies. Targets are always the
// OWNER (decided: the old code could pick any player in the party) and a random enemy chosen with the game's seeded RNG.

/// <summary>
/// "Debuff 1 to one enemy and to yourself when combat starts." Applied at the first turn start like Bag of Marbles (enemies exist
/// then), the same timing the old mod used ("turn == 1"). Per node: the amount grows, still one enemy.
/// </summary>
internal abstract class SymmetricDebuff<T> : EffectHandler where T : PowerModel
{
    public override async Task OnBeforeTurnStart(PlayerChoiceContext ctx, int turn)
    {
        if (turn > 1) return;
        var amount = Rolled();   // one roll, then the same amount lands on both sides: that is the gamble
        if (amount <= 0) return;
        var enemy = PickEnemy();
        if (enemy != null) await PowerCmd.Apply<T>(ctx, enemy, amount, Me, null, false);
        await GrantPower<T>(ctx, amount);   // the owner takes it too: that is the cost of the gamble
    }
}

internal sealed class SymmetricVulnerable : SymmetricDebuff<VulnerablePower> { public override string Id => "symmetric_vulnerable"; }
internal sealed class SymmetricWeak : SymmetricDebuff<WeakPower> { public override string Id => "symmetric_weak"; }

/// <summary>
/// Each turn, every perk has a 50% chance (a proc) to put Vulnerable, Weak or Frail 1 on either one random enemy or the owner,
/// a coin flip each. Same order of choices as the old Chaotic: chance, then side, then target, then which debuff.
/// </summary>
internal sealed class RandomDebuff : EffectHandler
{
    public override string Id => "random_debuff";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var procs = (int)Rolled();   // each success is one more debuff
        for (var i = 0; i < procs; i++)
        {
            var onSelf = Host.Rolls.Next(Id, 2) == 0;
            var target = onSelf ? Me : PickEnemy();
            if (target == null) continue;
            switch (Host.Rolls.Next(Id, 3))
            {
                case 0: await PowerCmd.Apply<VulnerablePower>(ctx, target, 1, Me, null, false); break;
                case 1: await PowerCmd.Apply<WeakPower>(ctx, target, 1, Me, null, false); break;
                default: await PowerCmd.Apply<FrailPower>(ctx, target, 1, Me, null, false); break;
            }
        }
    }
}

/// <summary>
/// Kill a Vulnerable enemy and its Vulnerable, Weak, Frail and Poison jump to a random other living enemy at full amount. The
/// debuffs are read BEFORE anything else happens: AfterDamageGiven runs before the death is processed, so they are still on it.
/// The copies name the receiving enemy as their own applier, so they don't read as fresh player debuffs (the old mod's trick).
/// </summary>
internal sealed class SpreadDebuffsOnKill : EffectHandler
{
    public override string Id => "spread_debuffs_on_kill";

    public override async Task OnDamageGiven(PlayerChoiceContext ctx, DamageResult result, Creature target)
    {
        if (!result.WasTargetKilled || !target.IsEnemy) return;

        var vulnerable = target.GetPowerAmount<VulnerablePower>();
        if (vulnerable <= 0) return;   // no Vulnerable, no spread
        var weak = target.GetPowerAmount<WeakPower>();
        var frail = target.GetPowerAmount<FrailPower>();
        var poison = target.GetPowerAmount<PoisonPower>();

        var hosts = Enemies().Where(e => !ReferenceEquals(e, target) && e.IsAlive).ToList();
        if (hosts.Count == 0) return;
        var next = hosts[Host.Rolls.Next(Id, hosts.Count)];

        await PowerCmd.Apply<VulnerablePower>(ctx, next, vulnerable, next, null, false);
        if (weak > 0) await PowerCmd.Apply<WeakPower>(ctx, next, weak, next, null, false);
        if (frail > 0) await PowerCmd.Apply<FrailPower>(ctx, next, frail, next, null, false);
        if (poison > 0) await PowerCmd.Apply<PoisonPower>(ctx, next, poison, next, null, false);
    }
}
