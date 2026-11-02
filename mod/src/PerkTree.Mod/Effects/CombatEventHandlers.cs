using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace PerkTree.Mod.Effects;

// Effects that react to the owner dealing damage. AfterDamageGiven runs once per target hit, so an area attack that kills three
// enemies counts three. A kill counts only when the hook names the owner as the dealer (unchecked: whether poison or thorns kills do).

/// <summary>Gold for each enemy you kill.</summary>
internal sealed class KillGold : EffectHandler
{
    public override string Id => "kill_gold";
    public override async Task OnDamageGiven(PlayerChoiceContext ctx, DamageResult result, Creature target)
    {
        if (result.WasTargetKilled && target.IsEnemy) await PlayerCmd.GainGold(Rolled(), Owner, false);
    }
}

/// <summary>Block for each enemy you kill.</summary>
internal sealed class KillBlock : EffectHandler
{
    public override string Id => "kill_block";
    public override async Task OnDamageGiven(PlayerChoiceContext ctx, DamageResult result, Creature target)
    {
        if (result.WasTargetKilled && target.IsEnemy) await GrantBlock(Rolled());
    }
}

/// <summary>A chance for 1 Energy whenever your damage gets past an enemy's block; each perk rolls on its own.</summary>
internal sealed class HitEnergyChance : EffectHandler
{
    public override string Id => "hit_energy_chance";
    public override async Task OnDamageGiven(PlayerChoiceContext ctx, DamageResult result, Creature target)
    {
        if (result.UnblockedDamage > 0 && target.IsEnemy) await GrantEnergy(Rolled());
    }
}

/// <summary>
/// Swift Defense: each time you take damage that gets past your Block, a chance to gain Block straight away. Every perk rolls on
/// its own. The hit has already landed, so this never softens that hit; it helps against the rest of a multi-hit attack and the
/// next attacker. Any damage to you counts, your own perks' too (Immolating, Frenzied). Block cannot hurt you, so it can't loop.
/// </summary>
internal sealed class DamagedBlockChance : EffectHandler
{
    public override string Id => "damaged_block_chance";
    public override Task OnDamaged(PlayerChoiceContext ctx, DamageResult result)
        => result.UnblockedDamage > 0 ? GrantBlock(Rolled()) : Task.CompletedTask;
}
