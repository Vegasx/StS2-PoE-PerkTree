using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Effects that fire once as a combat begins. Powers go in at AfterRoomEntered (Bronze Scales, Vajra and Oddly Smooth Stone all
// do), block at BeforeCombatStart (Anchor), enemy debuffs at the first BeforeSideTurnStart (Bag of Marbles, Red Mask).

/// <summary>Base for "gain N of a power when combat starts". The amount is the effect's total unless a subclass says otherwise.</summary>
internal abstract class CombatStartPower<T> : EffectHandler where T : PowerModel
{
    protected virtual decimal Granted => Rolled();
    public override Task OnCombatRoomEntered(PlayerChoiceContext ctx) => GrantPower<T>(ctx, Granted);
}

/// <summary>Base for the defensive start powers that start_power_boost makes bigger (only when the perk itself is owned).</summary>
internal abstract class BoostedStartPower<T> : CombatStartPower<T> where T : PowerModel
{
    protected override decimal Granted => ModifierMath.BoostedStartAmount(Rolled(), Host.Effective("start_power_boost"));
}

internal sealed class StartStrength : CombatStartPower<StrengthPower> { public override string Id => "start_strength"; }
internal sealed class StartDexterity : CombatStartPower<DexterityPower> { public override string Id => "start_dexterity"; }
internal sealed class StartRegen : CombatStartPower<RegenPower> { public override string Id => "start_regen"; }
internal sealed class StartArtifact : BoostedStartPower<ArtifactPower> { public override string Id => "start_artifact"; }
internal sealed class StartBuffer : BoostedStartPower<BufferPower> { public override string Id => "start_buffer"; }
internal sealed class StartBlur : BoostedStartPower<BlurPower> { public override string Id => "start_blur"; }

/// <summary>Vulnerable on every enemy as the first turn begins. Idiom: Bag of Marbles (BeforeSideTurnStart, turn 1 or earlier).</summary>
internal sealed class StartVulnerableEnemies : EffectHandler
{
    public override string Id => "start_vulnerable_enemies";
    public override Task OnBeforeTurnStart(PlayerChoiceContext ctx, int turn)
        => turn <= 1 ? GrantPowerToEnemies<VulnerablePower>(ctx, Rolled()) : Task.CompletedTask;
}

/// <summary>Strength for every 300 gold carried, when combat starts. Rounds down.</summary>
internal sealed class GoldStrength : EffectHandler
{
    private const int GoldPerStep = 300;

    public override string Id => "gold_strength";
    public override Task OnCombatRoomEntered(PlayerChoiceContext ctx)
        => GrantPower<StrengthPower>(ctx, (Owner.Gold / GoldPerStep) * Rolled());
}

/// <summary>Block per relic carried when combat starts. Idiom: Anchor for the block itself.</summary>
internal sealed class BlockPerRelic : EffectHandler
{
    // "max 8" in the old prefix. Kept as the cap on the TOTAL; whether it should scale with the number of nodes is a balance call (see open-decisions).
    private const decimal MaxBlock = 8m;

    public override string Id => "block_per_relic";
    public override Task OnBeforeCombat() => GrantBlock(Math.Min(Owner.Relics.Count * Rolled(), MaxBlock));
}
