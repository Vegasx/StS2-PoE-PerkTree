using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Gold;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Entities.Powers;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.ValueProps;

namespace PerkTree.Mod.Effects;

// The cost keystones: powerful gambles with a built-in price. The price is a plain constant here (it is the "cost" in cost keystone,
// and tuning it is a balance call); the benefit goes through Rolled() like every other perk, so it can have a chance.

/// <summary>Immolating: +Energy every turn, and HP lost at the start of every combat. The HP loss CAN kill you (decided).</summary>
internal sealed class EnergyForHp : EffectHandler
{
    private const decimal HpCost = 5m;
    public override string Id => "energy_for_hp";

    public override async Task OnCombatRoomEntered(PlayerChoiceContext ctx)
    {
        // Real HP loss: Unblockable (Block can't soak a price you chose to pay) and Unpowered (Strength/Vulnerable don't scale it).
        await CreatureCmd.Damage(ctx, Me, HpCost, ValueProp.Unblockable | ValueProp.Unpowered, Me);
    }

    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) => GrantEnergy(Rolled());
}

/// <summary>Overclocked: +Energy every turn, and Max HP lost at the start of every combat for the rest of the run.</summary>
internal sealed class EnergyForMaxHp : EffectHandler
{
    private const decimal MaxHpCost = 1m;
    public override string Id => "energy_for_max_hp";

    public override async Task OnCombatRoomEntered(PlayerChoiceContext ctx)
    {
        // Same call the game's own Terminal relic makes when a room is entered. Never take the last point of Max HP: that would be
        // a hidden death sentence the player can't see coming from the tooltip.
        if (Me.MaxHp > MaxHpCost) await CreatureCmd.LoseMaxHp(ctx, Me, MaxHpCost, false);
    }

    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) => GrantEnergy(Rolled());
}

/// <summary>
/// Echoing: each turn a random card in hand gains Replay 1 (the game's own Replay, so it plays twice); playing it costs you Vulnerable 1
/// and Frail 1. The Replay is taken away again once the card is played or the turn ends, so it never builds up on a card.
/// </summary>
internal sealed class ReplayGrant : EffectHandler
{
    private CardModel? _marked;
    public override string Id => "replay_grant";
    public override void ResetCombat() => _marked = null;

    private void Unmark()
    {
        if (_marked is { BaseReplayCount: > 0 }) _marked.BaseReplayCount--;
        _marked = null;
    }

    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        Unmark();
        if (Rolled() <= 0) return Task.CompletedTask;
        var hand = Owner.PlayerCombatState?.Hand.Cards;
        if (hand == null) return Task.CompletedTask;
        // Status and Curse cards are junk to replay (most can't even be played), so they are never picked.
        var eligible = hand.Where(c => c.Type is not (CardType.Status or CardType.Curse)).ToList();
        if (eligible.Count == 0) return Task.CompletedTask;
        _marked = eligible[Host.Rolls.Next(Id, eligible.Count)];
        _marked.BaseReplayCount++;
        return Task.CompletedTask;
    }

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (_marked == null || !ReferenceEquals(play.Card, _marked)) return;
        Unmark();
        await GrantPower<VulnerablePower>(ctx, 1m);
        await GrantPower<FrailPower>(ctx, 1m);
    }

    public override Task OnTurnEnding(PlayerChoiceContext ctx) { Unmark(); return Task.CompletedTask; }
}

/// <summary>
/// Eroding: each turn, move one enemy power one step toward zero, picked at random with the game's seeded RNG. Buffs and debuffs both,
/// including the ones your teammates put there; that is the gamble. Only visible, stacking powers are candidates.
/// </summary>
internal sealed class EnemyStrip : EffectHandler
{
    public override string Id => "enemy_strip";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var steps = (int)Rolled();
        for (var i = 0; i < steps; i++)
        {
            var candidates = new List<PowerModel>();
            foreach (var enemy in Enemies())
                foreach (var power in enemy.Powers)
                    if (power.IsVisible && power.StackType == PowerStackType.Counter && power.Amount != 0)
                        candidates.Add(power);
            if (candidates.Count == 0) return;
            var pick = candidates[Host.Rolls.Next(Id, candidates.Count)];
            // The enemy is its own applier, so none of the owner's "when you apply" perks mistake this for something they did.
            await PowerCmd.ModifyAmount(ctx, pick, -Math.Sign(pick.Amount), pick.Owner, null, false);
        }
    }
}

/// <summary>
/// Bribing: every enemy attack on you deals less damage and costs Gold. Both the discount and the price are applied per hit, so a
/// five-hit attack costs five times. With less Gold than the price there is no discount and no charge. This one cannot roll a chance:
/// the discount is worked out inside the game's damage calculation, which also runs for previews.
/// </summary>
internal sealed class GoldArmor : EffectHandler
{
    private const int GoldPerHit = 5;
    public override string Id => "gold_armor";

    public override bool ModifiesIncomingDamage => true;

    // Enemy attacks carry the Move flag; poison, thorns and the like do not, so they are not "hits".
    private bool Applies(Creature? dealer, ValueProp props)
        => dealer is { IsEnemy: true } && (props & ValueProp.Move) != 0 && Owner.Gold >= GoldPerHit;

    public override decimal IncomingDamageAdjust(Creature? dealer, ValueProp props) => Applies(dealer, props) ? -Amount : 0m;

    public override async Task OnHitBy(PlayerChoiceContext ctx, DamageResult result, ValueProp props, Creature? dealer)
    {
        if (Applies(dealer, props)) await PlayerCmd.LoseGold(GoldPerHit, Owner, GoldLossType.Spent);
    }
}
