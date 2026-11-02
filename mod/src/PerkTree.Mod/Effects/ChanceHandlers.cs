using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;

namespace PerkTree.Mod.Effects;

// Effects that roll dice. Every roll goes through Rolled(), the game's seeded RNG, so all co-op clients get the same result.

/// <summary>A chance to draw a card when you play a Skill. Each perk rolls on its own, and a chance past 100% draws extra.</summary>
internal sealed class SkillDrawChance : EffectHandler
{
    public override string Id => "skill_draw_chance";

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (play.Card.Type == CardType.Skill) await Draw(ctx, Rolled());
    }
}
