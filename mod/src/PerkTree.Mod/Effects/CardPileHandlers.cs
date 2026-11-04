using MegaCrit.Sts2.Core.CardSelection;
using MegaCrit.Sts2.Core.Commands;
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
/// away with it. Only a success uses the turn's "once" up: a failed roll lets the next Curse drawn that turn try again. Rides AfterCardDrawn, which the game calls inside the draw loop, so the grant is awaited in place.
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
        var amount = Rolled();
        if (amount <= 0) return;   // failed roll: not used up
        _lastTurn = turn;
        if (Host.Rolls.Next(Id, 2) == 0) await GrantPower<StrengthPower>(ctx, amount);
        else await GrantPower<DexterityPower>(ctx, amount);
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

// ---- start-of-turn hand choices and the free-attack return ------------------------------------------------------------

/// <summary>
/// Clean: at the start of your turn, a chance to EXHAUST up to some cards from your hand, your choice (you may pick none). Each perk
/// rolls on its own and adds its number to how many you may exhaust. The choice runs through the turn-start choice context, so in
/// co-op it is asked like any card's own "choose a card" prompt.
/// </summary>
internal sealed class TurnExhaustChoice : EffectHandler
{
    public override string Id => "turn_exhaust_chance";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var count = (int)Math.Floor(Rolled());
        var hand = Owner.PlayerCombatState?.Hand.Cards;
        if (count <= 0 || hand == null || hand.Count == 0) return;
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.ExhaustSelectionPrompt, 0, Math.Min(count, hand.Count));
        var picked = await CardSelectCmd.FromHand(ctx, Owner, prefs, null, Host);
        foreach (var card in picked.ToList()) await CardCmd.Exhaust(ctx, card, false, false);
    }
}

/// <summary>Picky: like Clean, but DISCARDS. Discarding goes through the game's own discard, so Cycling and the other discard perks react.</summary>
internal sealed class TurnDiscardChoice : EffectHandler
{
    public override string Id => "turn_discard_chance";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var count = (int)Math.Floor(Rolled());
        var hand = Owner.PlayerCombatState?.Hand.Cards;
        if (count <= 0 || hand == null || hand.Count == 0) return;
        var prefs = new CardSelectorPrefs(CardSelectorPrefs.DiscardSelectionPrompt, 0, Math.Min(count, hand.Count));
        var picked = await CardSelectCmd.FromHandForDiscard(ctx, Owner, prefs, null, Host);
        var cards = picked.ToList();
        if (cards.Count > 0) await CardCmd.Discard(ctx, cards);
    }
}

/// <summary>
/// Savage: a zero-cost Attack you play can come straight back to your hand instead of going to the discard pile, like the Feral power
/// (it uses the same game hook for where a played card ends up, so the card never visits the discard pile). Rules:
///  - every eligible card gets its own roll, and the trigger is only spent when a roll SUCCEEDS, so it keeps trying until one
///    does but never returns more than one card a turn;
///  - a card that would have Exhausted comes back too (it is not exhausted this time; played again, it exhausts as normal);
///  - X-cost cards and cards played for you by other effects never count.
/// </summary>
internal sealed class FreeAttackReturn : EffectHandler
{
    private bool _used;
    public override string Id => "free_attack_return_chance";
    public override void ResetCombat() => _used = false;
    public override Task OnTurnStart(PlayerChoiceContext ctx, int turn) { _used = false; return Task.CompletedTask; }

    public override CardLocation ModifyCardResult(CardModel card, bool isAutoPlay, ResourceInfo resources, CardLocation location)
    {
        if (_used || isAutoPlay || card.Type != CardType.Attack || card.EnergyCost.CostsX || resources.EnergySpent != 0) return location;
        // Only a card headed for the discard pile or the Exhaust pile can be brought back; anything else (already Hand, removed from combat) is left alone.
        if (location.pileType != PileType.Discard && location.pileType != PileType.Exhaust) return location;
        if (Rolled() <= 0) return location;
        _used = true;   // spent only by a success
        return new CardLocation(location.player, PileType.Hand, location.position);
    }
}
