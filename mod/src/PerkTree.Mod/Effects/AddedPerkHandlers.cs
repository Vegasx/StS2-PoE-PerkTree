using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rewards;

namespace PerkTree.Mod.Effects;

// Perks added after the original audit (the design doc's 99 effects), in the order they were asked for.

/// <summary>Healing: restore HP at the start of every turn. Same heal call as Vital (Blood Vial's), but every turn instead of turn 1.</summary>
internal sealed class TurnHeal : EffectHandler
{
    public override string Id => "turn_heal";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var heal = Rolled();
        if (heal > 0) await CreatureCmd.Heal(Me, heal, true);
    }
}

/// <summary>Focal: when you gain Focus, a chance to gain more. The bonus is itself a Focus gain, so a flag stops it triggering itself.</summary>
internal sealed class FocusGainChance : EffectHandler
{
    private bool _gaining;
    public override string Id => "focus_gain_chance";
    public override void ResetCombat() => _gaining = false;

    public override async Task OnPowerChanged(PlayerChoiceContext ctx, PowerModel power, decimal amount)
    {
        if (_gaining || power is not FocusPower || amount <= 0) return;
        var extra = Rolled();
        if (extra <= 0) return;
        _gaining = true;
        try { await GrantPower<FocusPower>(ctx, extra); }
        finally { _gaining = false; }
    }
}

/// <summary>
/// Supply: when you play a card, a chance to add a copy of it to your hand. The copy is Ethereal and Exhausts, so it is a one-shot extra
/// play. Made the way Dual Wield and Anger make theirs (CreateClone, then add it as a generated card). The copy can itself trigger
/// Supply when you play it, which needs a play from you each time, so it can't run away on its own.
/// </summary>
internal sealed class PlayCopyChance : EffectHandler
{
    public override string Id => "play_copy_chance";

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        var copies = (int)Rolled();
        for (var i = 0; i < copies; i++)
        {
            var copy = play.Card.CreateClone();
            copy.AddKeyword(CardKeyword.Ethereal);
            copy.AddKeyword(CardKeyword.Exhaust);
            await CardPileCmd.AddGeneratedCardToCombat(copy, PileType.Hand, Owner, CardPilePosition.Bottom);
        }
    }
}

/// <summary>
/// Cash Flow: after a combat, an extra Gold reward on the rewards screen. All your Cash Flow perks roll and add up into ONE reward, and it is
/// its own entry next to the game's normal gold, never merged into it.
/// </summary>
internal sealed class CombatGoldReward : EffectHandler
{
    public override string Id => "combat_gold_reward";

    public override bool ModifyRewards(List<Reward> rewards)
    {
        var gold = (int)Math.Floor(Rolled());
        if (gold <= 0) return false;
        rewards.Add(new GoldReward(gold, Owner, false));
        return true;
    }
}
