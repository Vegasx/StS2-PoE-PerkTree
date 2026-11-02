using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;

namespace PerkTree.Mod.Effects;

// The character mechanics: Shivs (Silent), Orbs (Defect), Osty summons (Necrobinder), Stars and Forge (Regent). Each perk calls the same game
// command the character's own cards and relics call (Shiv.CreateInHand, OrbCmd.Channel, OstyCmd.Summon, PlayerCmd.GainStars...), and the
// benefit goes through Rolled(), so every one of them can have a chance. A perk can sit in another character's tree on purpose, but it
// only does something if that character can trigger the mechanic.

// ---- Silent: Shivs ----------------------------------------------------------------------------------------------

/// <summary>Flurrying: playing a Shiv can add more Shivs to your hand. The new Shivs are created, not played, so this can't loop.</summary>
internal sealed class ShivDuplicateChance : EffectHandler
{
    public override string Id => "shiv_duplicate_chance";

    public override async Task OnCardPlayed(PlayerChoiceContext ctx, CardPlay play)
    {
        if (play.Card is not Shiv) return;
        var count = (int)Rolled();
        var combat = Me.CombatState;
        if (count > 0 && combat != null) await Shiv.CreateInHand(Owner, count, combat, Owner);
    }
}

/// <summary>Retrieving: an Exhausted Shiv can come straight back to your hand.</summary>
internal sealed class ShivReturnChance : EffectHandler
{
    public override string Id => "shiv_return_chance";

    public override async Task OnCardExhausted(PlayerChoiceContext ctx, CardModel card, bool causedByEthereal)
    {
        if (card is not Shiv || Rolled() <= 0) return;
        await CardPileCmd.Add(card, PileType.Hand, CardPilePosition.Bottom, null, false);
    }
}

// ---- Defect: Orbs -----------------------------------------------------------------------------------------------

/// <summary>Amplified: channeling an Orb can channel more random Orbs. Their own channeling fires this again, hence the flag.</summary>
internal sealed class OrbChannelChance : EffectHandler
{
    private bool _channeling;
    public override string Id => "orb_channel_chance";
    public override void ResetCombat() => _channeling = false;

    public override async Task OnOrbChanneled(PlayerChoiceContext ctx, OrbModel orb)
    {
        if (_channeling) return;
        var count = (int)Rolled();
        if (count <= 0) return;
        _channeling = true;
        try { for (var i = 0; i < count; i++) await ChannelRandomOrb(ctx); }
        finally { _channeling = false; }
    }
}

/// <summary>
/// Supercharged: gain Focus while every Orb slot is full. It is rolled each time the slots BECOME full and taken away again as soon as
/// they are not, so the Focus never outlives the full slots.
/// </summary>
internal sealed class FocusWhenFull : EffectHandler
{
    private bool _wasFull;
    private decimal _given;
    public override string Id => "focus_when_full";

    public override void ResetCombat() { _wasFull = false; _given = 0m; }

    public override async Task OnOrbsChanged(PlayerChoiceContext ctx)
    {
        var queue = Owner.PlayerCombatState?.OrbQueue;
        if (queue == null) return;
        var full = queue.Capacity > 0 && queue.Orbs.Count >= queue.Capacity;
        if (full == _wasFull) return;
        _wasFull = full;
        if (full)
        {
            _given = Rolled();
            await GrantPower<FocusPower>(ctx, _given);
        }
        else if (_given != 0m)
        {
            var take = _given;
            _given = 0m;
            await PowerCmd.Apply<FocusPower>(ctx, Me, -take, Me, null, false);
        }
    }
}

/// <summary>Preheated: Orbs channeled before your first turn. Same hook the game's Cracked Core uses (BeforeSideTurnStart).</summary>
internal sealed class StartChannelOrb : EffectHandler
{
    public override string Id => "start_channel_orb";

    public override async Task OnBeforeTurnStart(PlayerChoiceContext ctx, int turn)
    {
        if (turn > 1) return;
        var count = (int)Rolled();
        for (var i = 0; i < count; i++) await ChannelRandomOrb(ctx);
    }
}

// ---- Necrobinder: Osty ------------------------------------------------------------------------------------------

/// <summary>Necromantic: Block whenever you Summon.</summary>
internal sealed class SummonBlock : EffectHandler
{
    public override string Id => "summon_block";
    public override Task OnSummon(PlayerChoiceContext ctx, decimal amount) => GrantBlock(Rolled());
}

/// <summary>Bonebound: Summon at the start of every turn (the hook Phylactery Unbound uses).</summary>
internal sealed class SummonEachTurn : EffectHandler
{
    public override string Id => "summon_each_turn";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var amount = Rolled();
        if (amount > 0) await OstyCmd.Summon(ctx, Owner, amount, Host);
    }
}

/// <summary>
/// Vengeful: at turn start, Summon a multiple of the HP you lost since your last turn (the perk's number IS the multiple: 1.5 = 1.5x).
/// Counts every HP drop, like Retaliating. Rounded down, so what you summon is a whole number whatever the multiple.
/// </summary>
internal sealed class TurnSummonFromHpLost : EffectHandler
{
    private decimal _lost;
    public override string Id => "turn_summon_from_hp_lost";
    public override void ResetCombat() => _lost = 0m;

    public override Task OnHpChanged(decimal delta)
    {
        if (delta < 0) _lost += -delta;
        return Task.CompletedTask;
    }

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var lost = _lost;
        _lost = 0m;
        if (lost <= 0m) return;
        var amount = Math.Floor(lost * Rolled());
        if (amount > 0) await OstyCmd.Summon(ctx, Owner, amount, Host);
    }
}

/// <summary>Empathic: at turn start, Block equal to the HP your Osty lost since your last turn.</summary>
internal sealed class TurnBlockFromSummonHp : EffectHandler
{
    private decimal _ostyLost;
    public override string Id => "turn_block_from_summon_hp";
    public override void ResetCombat() => _ostyLost = 0m;

    public override Task OnOstyHpChanged(decimal delta)
    {
        if (delta < 0) _ostyLost += -delta;
        return Task.CompletedTask;
    }

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var lost = _ostyLost;
        _ostyLost = 0m;
        if (lost > 0m) await GrantBlock(lost * Rolled());
    }
}

// ---- Regent: Stars and Forge ------------------------------------------------------------------------------------

/// <summary>Starlit: Stars at the start of every turn.</summary>
internal sealed class StarEachTurn : EffectHandler
{
    public override string Id => "star_each_turn";

    public override async Task OnTurnStart(PlayerChoiceContext ctx, int turn)
    {
        var stars = Rolled();
        if (stars > 0) await PlayerCmd.GainStars(stars, Owner);
    }
}

/// <summary>Reforging: Stars whenever you Forge.</summary>
internal sealed class ForgeStar : EffectHandler
{
    public override string Id => "forge_star";

    public override async Task OnForge(decimal amount)
    {
        var stars = Rolled();
        if (stars > 0) await PlayerCmd.GainStars(stars, Owner);
    }
}

/// <summary>Tributary: whenever you Forge, draw cards. The Forge hook carries no choice context, so this draws like the potion perks do.</summary>
internal sealed class ForgeDrawChance : EffectHandler
{
    public override string Id => "forge_draw_chance";
    public override Task OnForge(decimal amount) => Draw(NoChoice(), Rolled());
}

/// <summary>Regal: when you spend Stars, get some back (never more than you spent).</summary>
internal sealed class StarRefundChance : EffectHandler
{
    public override string Id => "star_refund_chance";

    public override async Task OnStarsSpent(int amount)
    {
        var refund = Math.Min(Rolled(), amount);
        if (refund > 0) await PlayerCmd.GainStars(refund, Owner);
    }
}

/// <summary>Bountiful: when you gain Stars, gain more. The bonus is itself a gain of Stars, so a flag stops it triggering itself.</summary>
internal sealed class StarGainChance : EffectHandler
{
    private bool _gaining;
    public override string Id => "star_gain_chance";
    public override void ResetCombat() => _gaining = false;

    public override async Task OnStarsGained(int amount)
    {
        if (_gaining) return;
        var extra = Rolled();
        if (extra <= 0) return;
        _gaining = true;
        try { await PlayerCmd.GainStars(extra, Owner); }
        finally { _gaining = false; }
    }
}
