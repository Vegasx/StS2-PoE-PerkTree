using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Rewards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Effects that fire when you use a potion in combat. The audit named BeforePotionUsed; these use AfterPotionUsed instead (the
// potion's own effect has resolved, so enemies it killed are gone and the debuffs land on whoever is left). Every amount goes
// through PotionAmount(), which applies potion_boost and potion_double.

internal sealed class PotionBlock : EffectHandler
{
    public override string Id => "potion_block";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantBlock(PotionAmount(Rolled()));
}

internal sealed class PotionStrength : EffectHandler
{
    public override string Id => "potion_strength";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantPower<StrengthPower>(NoChoice(), PotionAmount(Rolled()));
}

internal sealed class PotionDexterity : EffectHandler
{
    public override string Id => "potion_dexterity";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantPower<DexterityPower>(NoChoice(), PotionAmount(Rolled()));
}

internal sealed class PotionVulnerable : EffectHandler
{
    public override string Id => "potion_vulnerable";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantPowerToEnemies<VulnerablePower>(NoChoice(), PotionAmount(Rolled()));
}

internal sealed class PotionWeak : EffectHandler
{
    public override string Id => "potion_weak";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantPowerToEnemies<WeakPower>(NoChoice(), PotionAmount(Rolled()));
}

/// <summary>A chance of Buffer on potion use; each perk rolls on its own. potion_boost and potion_double change how much Buffer each proc gives, not how likely it is.</summary>
internal sealed class PotionBuffer : EffectHandler
{
    public override string Id => "potion_buffer";
    public override async Task OnPotionUsed(PotionModel potion, Creature? target)
    {
        var procs = Rolled();
        if (procs > 0) await GrantPower<BufferPower>(NoChoice(), PotionAmount(1m) * procs);
    }
}

/// <summary>
/// Acidic: drinking a potion splashes every enemy. Goes through PotionAmount like the other potion-use perks, so Refined and
/// Concentrated make it hit harder. AfterPotionUsed, so a potion that already killed something doesn't get it hit twice.
/// </summary>
internal sealed class PotionAcid : EffectHandler
{
    public override string Id => "potion_acid";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => DamageAllEnemies(NoChoice(), PotionAmount(Rolled()));
}

/// <summary>
/// Potionholic: extra potions on the rewards screen after combat. Each one is the game's own PotionReward, so it draws from the
/// native potion pool (rarity odds, character pool, blacklist) with the player's reward RNG, shows up as its own button and
/// is simply left on the screen if the belt is full, exactly like the usual potion drop.
/// </summary>
internal sealed class PotionFind : EffectHandler
{
    public override string Id => "potion_find_chance";

    // A runaway stack (chance far past 100%) shouldn't flood the rewards screen.
    private const int MaxExtraPotions = 10;

    public override bool ModifyRewards(List<Reward> rewards)
    {
        var count = (int)Math.Min(Math.Floor(Rolled()), MaxExtraPotions);
        for (var i = 0; i < count; i++) rewards.Add(new PotionReward(Owner));
        return count > 0;
    }
}

/// <summary>
/// Recycler: a chance to get the potion you just used straight back. The game takes a potion out of its slot BEFORE it
/// resolves, so the refund puts a fresh copy of the same potion into the first empty slot. It happens at most once per use,
/// however many Recyclers succeed (two at 100% must not turn one potion into three), and it does nothing when the belt is
/// full, for instance after Entropic Brew filled it. Only potions used from the belt during a fight count: the hook never
/// reaches potions used on the map or at a merchant.
/// </summary>
internal sealed class PotionRefund : EffectHandler
{
    public override string Id => "potion_refund_chance";
    public override bool RunsWhenPotionEndsCombat => true;

    public override async Task OnPotionUsed(PotionModel potion, Creature? target)
    {
        if (Rolled() <= 0) return;

        var free = -1;
        for (var slot = 0; slot < Owner.MaxPotionCount; slot++)
            if (Owner.GetPotionAtSlotIndex(slot) == null) { free = slot; break; }
        if (free < 0) return;

        await PotionCmd.TryToProcure(potion.CanonicalInstance.ToMutable(), Owner, free);
    }
}

/// <summary>
/// Hidden Dose: drinking a potion poisons every enemy. Same shape as Corrosive and Diluting, so Refined and Concentrated
/// make it stronger. The applier is you, so the poison counts as yours for Toxic Ward, Poison Cloud and Envenomed.
/// </summary>
internal sealed class PotionPoison : EffectHandler
{
    public override string Id => "potion_poison";
    public override Task OnPotionUsed(PotionModel potion, Creature? target) => GrantPowerToEnemies<PoisonPower>(NoChoice(), PotionAmount(Rolled()));
}
