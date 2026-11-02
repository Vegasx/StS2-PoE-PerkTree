using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// Effects that react to cards moving between piles: exhaust, discard, draw, and the end-of-turn flush. Each one runs inside the
// game's own loop for that move and is awaited, which is what keeps co-op in step (the old mod desynced when these were detached).

/// <summary>Block whenever one of your cards is exhausted (Ironclad).</summary>
internal sealed class ExhaustBlock : EffectHandler
{
    public override string Id => "exhaust_block";
    public override Task OnCardExhausted(PlayerChoiceContext ctx, CardModel card, bool causedByEthereal) => GrantBlock(Rolled());
}

/// <summary>Draw a card whenever you discard one (Silent). Drawing from two empty piles does nothing, so this can't loop.</summary>
internal sealed class DiscardDraw : EffectHandler
{
    public override string Id => "discard_draw";
    public override Task OnCardDiscarded(PlayerChoiceContext ctx, CardModel card) => Draw(ctx, Rolled());
}

/// <summary>
/// The first time you DRAW a Curse each turn, +Strength or +Dexterity (a coin flip). Once a turn so a shuffle-heavy deck can't run
/// away with it. Rides AfterCardDrawn, which the game calls inside the draw loop, so the grant is awaited in place.
/// </summary>
internal sealed class CurseDrawStat : EffectHandler
{
    private int _lastTurn = -1;
    public override string Id => "curse_draw_stat";
    public override void ResetCombat() => _lastTurn = -1;

    public override async Task OnCardDrawn(PlayerChoiceContext ctx, CardModel card, bool fromHandDraw)
    {
        if (card.Type != CardType.Curse) return;
        var turn = Owner.PlayerCombatState?.TurnNumber ?? 0;
        if (turn == _lastTurn) return;
        _lastTurn = turn;
        if (Host.Rolls.Next(Id, 2) == 0) await GrantPower<StrengthPower>(ctx, Rolled());
        else await GrantPower<DexterityPower>(ctx, Rolled());
    }
}

/// <summary>
/// At the end of your turn, give single-turn Retain to random cards in hand (one per perk). Runs at BeforeFlushLate, after the
/// game's own retain effects have marked theirs, so it only touches cards that would otherwise be discarded.
/// </summary>
internal sealed class RetainRandom : EffectHandler
{
    public override string Id => "retain_random";

    public override Task OnBeforeFlush(PlayerChoiceContext ctx)
    {
        var hand = Owner.PlayerCombatState?.Hand.Cards;
        if (hand == null) return Task.CompletedTask;

        var count = (int)Rolled();   // rolled once, not once per loop pass
        for (var i = 0; i < count; i++)
        {
            var eligible = hand.Where(c => !c.ShouldRetainThisTurn).ToList();
            if (eligible.Count == 0) break;
            eligible[Host.Rolls.Next(Id, eligible.Count)].GiveSingleTurnRetain();
        }
        return Task.CompletedTask;
    }
}

/// <summary>
/// Cursebound: a combat-start Strength / Dexterity / Intangible package that grows with the Curse cards in your RUN DECK (decided:
/// the old mod counted its own curse relics). Combat-created curses don't count. Each perk applies the package once, so two perks
/// double it, Intangible included.
/// </summary>
internal sealed class CurseCountRamp : EffectHandler
{
    public override string Id => "curse_count_ramp";

    public override async Task OnCombatRoomEntered(PlayerChoiceContext ctx)
    {
        var curses = Owner.Deck.Cards.Count(c => c.Type == CardType.Curse);
        var (strength, dexterity, intangible) = CurseRamp.For(curses);
        var packages = Rolled();   // one roll per perk for the whole package, so a failed perk gives none of it
        await GrantPower<StrengthPower>(ctx, strength * packages);
        await GrantPower<DexterityPower>(ctx, dexterity * packages);
        if (intangible) await GrantPower<IntangiblePower>(ctx, packages);
    }
}
