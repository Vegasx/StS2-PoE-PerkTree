using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;

namespace PerkTree.Mod.Effects;

/// <summary>
/// One trigger between the owner's turn starts. Mark it before issuing commands, so nested events cannot trigger it twice.
/// A failed roll still uses the first event; extra nodes change the amount, not the number of eligible events.
/// Reset before turn-start draws/exhausts, rather than in AfterPlayerTurnStartLate.
/// </summary>
internal abstract class FirstEventPerTurn : EffectHandler
{
    private bool _used;
    public override void ResetCombat() => _used = false;
    public override Task OnBeforeTurnStart(PlayerChoiceContext ctx, int turn)
    {
        _used = false;
        return Task.CompletedTask;
    }

    protected bool UseFirstEvent()
    {
        if (_used) return false;
        _used = true;
        return true;
    }
}

internal sealed class FirstSkillBlock : FirstEventPerTurn
{
    public override string Id => "first_skill_block";
    public override Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
        => play.Card.Type == CardType.Skill && UseFirstEvent() ? GrantBlock(Rolled()) : Task.CompletedTask;
}

internal sealed class FirstExhaustVigor : FirstEventPerTurn
{
    public override string Id => "first_exhaust_vigor";
    public override Task OnCardExhausted(PlayerChoiceContext ctx, CardModel card, bool causedByEthereal)
        => UseFirstEvent() ? GrantPower<VigorPower>(ctx, Rolled()) : Task.CompletedTask;
}

internal sealed class FirstDebuffBlock : FirstEventPerTurn
{
    public override string Id => "first_debuff_block";
    public override Task OnPowerAppliedToEnemy(PlayerChoiceContext ctx, PowerModel power, decimal amount, Creature target)
        // Classify the change, not the resulting stack: -1 Strength is a debuff even on an enemy with positive Strength.
        // Removing Poison/Weak is a buff and must not count as applying a debuff.
        => CombatManager.Instance.IsInProgress && amount != 0 && power.GetTypeForAmount(amount) == PowerType.Debuff
            && UseFirstEvent() ? GrantBlock(Rolled()) : Task.CompletedTask;
}

internal sealed class EndTurnEnergyBlock : EffectHandler
{
    public override string Id => "end_turn_energy_block";
    public override Task OnTurnEnding(PlayerChoiceContext ctx)
        => Owner.PlayerCombatState is { Energy: > 0 } ? GrantBlock(Rolled()) : Task.CompletedTask;
}

internal sealed class RestHealBonus : EffectHandler
{
    public override string Id => "rest_heal_bonus";
    public override decimal ModifyRestHeal(decimal amount) => amount + Amount;
}

internal sealed class ShopGold : EffectHandler
{
    public override string Id => "shop_gold";
    public override async Task OnRoomEntered(AbstractRoom room)
    {
        if (room is not MerchantRoom || !Me.IsAlive) return;
        // The saved counter and the game's Gold are captured by the same SaveRun. A new host on reload cannot pay twice.
        var floor = (long)Owner.RunState.TotalFloor + 1;
        var previous = Count("paid_floor");
        if (floor <= previous) return;
        CountUp("paid_floor", floor - previous);
        var gold = Rolled();
        if (gold > 0) await PlayerCmd.GainGold(gold, Owner, false);
    }
}
