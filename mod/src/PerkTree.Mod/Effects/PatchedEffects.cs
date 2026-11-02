using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.ValueProps;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Effects with no game hook to ride, so a small Harmony patch (EffectPatches.cs) calls into these classes. Keeping the logic here and
// the patch thin means the rules read in one place next to the other effects. Each patch bails on its first line for anyone without
// the perk.

/// <summary>A chance that a card about to be exhausted is discarded instead. Each perk rolls on its own; any proc saves the card.</summary>
internal sealed class ExhaustDodge : EffectHandler
{
    public override string Id => "exhaust_dodge";
    public bool Dodges() => Rolled() > 0;
}

/// <summary>A card that would exhaust because of Ethereal is discarded instead. Presence only; the patch checks for it.</summary>
internal sealed class EtherealSave : EffectHandler
{
    public override string Id => "ethereal_save";
}

/// <summary>
/// Discharging: whenever you gain Energy (beyond the turn refill, which the game does with SetEnergy), deal damage to all enemies,
/// per Energy gained. Energy from other perks counts, which is the point. Re-entrancy is closed: energy gained WHILE discharging
/// (a perk that gives Energy when you deal damage, say) doesn't discharge again, or a certain proc would never stop.
/// </summary>
internal sealed class EnergyDischarge : EffectHandler
{
    private bool _discharging;
    public override string Id => "energy_discharge";
    public override void ResetCombat() => _discharging = false;

    public async Task Discharge(decimal energyGained)
    {
        if (_discharging || energyGained <= 0 || !Me.IsAlive) return;
        _discharging = true;
        try { await DamageAllEnemies(new BlockingPlayerChoiceContext(), Rolled() * energyGained); }
        finally { _discharging = false; }
    }
}

/// <summary>
/// Undying (Necrobinder): Doom no longer kills you at turn end. The patch takes your body out of Doom's kill list and arms a reprieve;
/// the next HP you lose while Doom still stands at or above your HP finishes you. Borrowed time, not immunity. Only your own body is
/// spared, never your summon.
/// </summary>
internal sealed class DoomDefer : EffectHandler
{
    private bool _armed;
    public override string Id => "doom_defer";
    public override void ResetCombat() => _armed = false;

    /// <summary>Called by the DoomKill patch for each creature on the kill list. True means spare this one (and the reprieve is armed).</summary>
    public bool Spares(Creature creature)
    {
        if (!ReferenceEquals(creature, Me)) return false;
        _armed = true;
        return true;
    }

    public override async Task OnHpChanged(decimal delta)
    {
        if (!_armed || delta >= 0 || !Me.IsAlive) return;
        // Still doomed? The game's own test is HP at or below the Doom amount. Healing above it lets the reprieve lapse.
        if (Me.GetPower<DoomPower>() is { } doom && Me.CurrentHp <= doom.Amount)
            await CreatureCmd.Kill(Me, false);
    }
}
