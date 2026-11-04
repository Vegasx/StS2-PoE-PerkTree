using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// The Vigor family. Vigor is spent when an attack uses it up (VigorPower.AfterAttack calls PowerCmd.ModifyAmount with the negative
// amount), which the host sees as AfterPowerAmountChanged on a VigorPower with a negative change.

/// <summary>
/// Base for "each Vigor point spent: a chance of something". Every Vigor point spent is one roll for EACH perk of the effect
/// (Vigor 8 with three perks = 24 rolls), and a chance past 100% gives sure successes. Each success gives the perk's amount.
/// Capped so a huge Vigor stack can't run away.
/// Does not fire for the turn-end conversion perks (Host.ConvertingVigor), as in the old mod.
/// </summary>
internal abstract class VigorSpentProc : EffectHandler
{
    private const int MaxPointsPerSpend = 50;
    private const decimal MaxPerSpend = 200m;

    // Frenzied: a failed roll gets one more try (the same thing proc_reroll does, but only for these perks).
    protected override int ExtraTries => Host.Effective("vigor_proc_double") > 0 ? 1 : 0;

    /// <summary>Give the summed amount of every roll that succeeded in one spend.</summary>
    protected abstract Task Succeed(PlayerChoiceContext ctx, decimal total);

    public override async Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount)
    {
        if (power is not VigorPower || amount >= 0 || Host.ConvertingVigor) return;
        var points = (int)Math.Min(-amount, MaxPointsPerSpend);
        var total = 0m;
        for (var i = 0; i < points && total < MaxPerSpend; i++) total += Rolled();
        if (total > 0) await Succeed(ctx, Math.Min(total, MaxPerSpend));
    }

    /// <summary>Applies a power to a random enemy, picked with the game's seeded RNG, once per proc.</summary>
    protected async Task GrantPowerToRandomEnemies<T>(PlayerChoiceContext ctx, decimal total) where T : PowerModel
    {
        for (var i = 0; i < (int)total; i++)
        {
            var enemies = Enemies();
            if (enemies.Count == 0) return;
            var target = enemies[Host.Rolls.Next(Id, enemies.Count)];
            await PowerCmd.Apply<T>(ctx, target, 1, Me, null, false);
        }
    }
}

internal sealed class VigorStrengthPct : VigorSpentProc
{
    public override string Id => "vigor_strength_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantPower<StrengthPower>(ctx, total);
}

internal sealed class VigorBlockPct : VigorSpentProc
{
    public override string Id => "vigor_block_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantBlock(total);
}

internal sealed class VigorEnergyPct : VigorSpentProc
{
    public override string Id => "vigor_energy_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantEnergy(total);
}

internal sealed class VigorRegenPct : VigorSpentProc
{
    public override string Id => "vigor_regen_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantPower<RegenPower>(ctx, total);
}

internal sealed class VigorVulnerablePct : VigorSpentProc
{
    public override string Id => "vigor_vulnerable_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantPowerToRandomEnemies<VulnerablePower>(ctx, total);
}

internal sealed class VigorWeakPct : VigorSpentProc
{
    public override string Id => "vigor_weak_pct";
    protected override Task Succeed(PlayerChoiceContext ctx, decimal total) => GrantPowerToRandomEnemies<WeakPower>(ctx, total);
}

/// <summary>
/// Frenzied (keystone): the Vigor-spending perks get an extra try on every failed roll (see <see cref="VigorSpentProc"/>), and the price is
/// that each Vigor point spent has a chance to deal damage to you. Both numbers come from the data: the perk's chance is the chance per
/// Vigor point and its amount is the damage. Real damage, not HP loss, so Block soaks it. It can kill you at 1 HP; that is the gamble.
/// Like the perks it speeds up, it ignores the turn-end conversions (Host.ConvertingVigor).
/// </summary>
internal sealed class VigorProcDouble : EffectHandler
{
    private const int MaxPointsPerSpend = 50;
    public override string Id => "vigor_proc_double";

    public override async Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount)
    {
        if (power is not VigorPower || amount >= 0 || Host.ConvertingVigor) return;
        var damage = RolledEach((int)Math.Min(-amount, MaxPointsPerSpend));
        if (damage > 0) await CreatureCmd.Damage(ctx, Me, damage, Flat, Me);
    }
}

/// <summary>
/// Congealing: at turn end, every unused Vigor point turns into Block, and the number on the node is how much Block per
/// Vigor (1 = one for one, 2 = double, 0.2 = one Block for every five Vigor). Each Vigor rolls the perk's chance on its own
/// and adds that node's amount, so the total is rounded down once at the end rather than per point (5 Vigor at 0.2 is 1 Block,
/// not 0). Several nodes add together. Marks the Vigor to be removed; the host removes it once all turn-end perks have seen it.
/// Counts at most 50 unused Vigor points per turn (see RolledEach).
/// </summary>
internal sealed class VigorUnusedToBlock : EffectHandler
{
    public override string Id => "vigor_unused_to_block";
    public override async Task OnTurnEnding(PlayerChoiceContext ctx)
    {
        var unused = Host.TurnEnd.UnusedVigor;
        if (unused <= 0) return;
        Host.TurnEnd.ConsumeVigor = true;
        await GrantBlock(Math.Floor(RolledEach(unused)));
    }
}

/// <summary>At turn end, each unused Vigor point has a chance of 1 Strength (one chance per perk), and the Vigor is used up.</summary>
internal sealed class VigorUnusedStrengthPct : EffectHandler
{
    private const int MaxRolls = 50;
    private const int MaxStrength = 200;
    public override string Id => "vigor_unused_strength_pct";

    public override async Task OnTurnEnding(PlayerChoiceContext ctx)
    {
        var unused = Math.Min(Host.TurnEnd.UnusedVigor, MaxRolls);
        if (unused <= 0) return;
        Host.TurnEnd.ConsumeVigor = true;
        var strength = 0m;
        for (var i = 0; i < unused && strength < MaxStrength; i++) strength += Rolled();
        await GrantPower<StrengthPower>(ctx, strength);
    }
}

/// <summary>
/// "Gaining X gives +1 more, at most 3 times a turn." One base for both users: Vigor (vigor_gain_amplify) and Strength/Dexterity
/// (gain_amplify). The bonus is itself a power gain, so it would trigger this again; the flag stops that loop.
/// Only a success counts toward the 3: a failed roll leaves the turn's limit untouched.
/// </summary>
internal abstract class GainAmplifyBase : EffectHandler
{
    private const int MaxPerTurn = 3;
    private int _usedThisTurn;
    private bool _applying;

    /// <summary>Is this the kind of power this perk amplifies?</summary>
    protected abstract bool Amplifies(PowerModel gained);

    protected abstract Task GrantBonus(PlayerChoiceContext ctx, PowerModel gained, decimal bonus);

    public override void ResetCombat() { _usedThisTurn = 0; _applying = false; }
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) { _usedThisTurn = 0; return Task.CompletedTask; }

    public override async Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount)
    {
        if (_applying || amount <= 0 || _usedThisTurn >= MaxPerTurn || !Amplifies(power)) return;
        var bonus = Rolled();
        if (bonus <= 0) return;   // failed roll: not counted
        _usedThisTurn++;
        _applying = true;
        try { await GrantBonus(ctx, power, bonus); }
        finally { _applying = false; }
    }
}

internal sealed class VigorGainAmplify : GainAmplifyBase
{
    public override string Id => "vigor_gain_amplify";
    protected override bool Amplifies(PowerModel gained) => gained is VigorPower;
    protected override Task GrantBonus(PlayerChoiceContext ctx, PowerModel gained, decimal bonus) => GrantPower<VigorPower>(ctx, bonus);
}

/// <summary>Resonant: gaining Strength or Dexterity gives +1 more (3 times a turn between them).</summary>
internal sealed class GainAmplify : GainAmplifyBase
{
    public override string Id => "gain_amplify";
    protected override bool Amplifies(PowerModel gained) => gained is StrengthPower or DexterityPower;

    protected override Task GrantBonus(PlayerChoiceContext ctx, PowerModel gained, decimal bonus)
        => gained is StrengthPower ? GrantPower<StrengthPower>(ctx, bonus) : GrantPower<DexterityPower>(ctx, bonus);
}
