using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Random;

namespace PerkTree.Mod.Effects;

/// <summary>
/// The ONLY source of randomness for perks. Every roll comes from the game's own seeded <see cref="Rng"/>, never from
/// System.Random, Rng.Chaotic, the clock or a Guid, because in co-op every client must roll the same numbers in the same
/// order or the simulations drift apart and the game disconnects.
///
/// How the streams are seeded (the same idiom the game's own relics use, e.g. Fur Coat and Byrdpip):
///   new Rng(player, modelId, extra) is a pure function of the RUN SEED, the player's SLOT index (not their NetId, so a
///   rejoin can't change it), a deterministic hash of the model id's name, and <c>extra</c>. We pass the effect id as the
///   model id and the combat's identity as <c>extra</c>, so each (player, effect, combat) gets its own stream.
///
/// Why a fresh stream per combat instead of one run-long stream: nothing has to be saved or restored (the game never
/// saves mid-combat), a reload replays a combat with exactly the same rolls, and one combat's draw count can never
/// shift the next combat's rolls. The cost is that two combats on one floor share a key unless told apart, hence the
/// per-floor ordinal below.
///
/// Rolls must only happen inside game hooks that every client runs, in the same order. Do not roll from UI code, tooltips
/// or previews: that would consume numbers on one machine only.
/// </summary>
internal sealed class PerkRng
{
    private readonly Player _owner;
    private readonly Dictionary<string, Rng> _streams = new(StringComparer.Ordinal);

    // Identifies the current combat. The floor is the same on every client; the ordinal separates two combats that
    // happen on the same floor (an event that starts a fight after a fight).
    private int _lastFloor = -1;
    private int _ordinalOnFloor;
    private ulong _combatKey;

    public PerkRng(Player owner) => _owner = owner;

    /// <summary>Call at BeforeCombatStart on every client: discards last combat's streams and keys the new combat.</summary>
    public void BeginCombat()
    {
        var floor = _owner.RunState.TotalFloor;
        _ordinalOnFloor = floor == _lastFloor ? _ordinalOnFloor + 1 : 0;
        _lastFloor = floor;
        _combatKey = (ulong)floor * 64UL + (ulong)Math.Min(_ordinalOnFloor, 63);
        _streams.Clear();
    }

    private Rng Stream(string effectId)
    {
        if (!_streams.TryGetValue(effectId, out var rng))
        {
            // The Rng constructor only reads ModelId.Entry (hashed deterministically), so a made-up category is fine.
            rng = new Rng(_owner, new ModelId("PERK", effectId), _combatKey);
            _streams[effectId] = rng;
        }
        return rng;
    }

    /// <summary>
    /// True with the given probability (0 to 1, in 0.01% steps). Certain and impossible outcomes don't consume a number;
    /// that is the same on every client because the probability itself comes from synced state.
    /// </summary>
    public bool Chance(string effectId, decimal probability)
    {
        var basisPoints = ChanceMath.ToBasisPoints(probability); // integers only, see ChanceMath
        if (basisPoints <= 0) return false;
        if (basisPoints >= ChanceMath.Scale) return true;
        return Stream(effectId).NextInt(ChanceMath.Scale) < basisPoints;
    }

    /// <summary>This effect's stream itself, for game functions that want an Rng (OrbModel.GetRandomOrb). Same rules as every roll here.</summary>
    public Rng StreamOf(string effectId) => Stream(effectId);

    /// <summary>A number in [0, maxExclusive) from this effect's stream.</summary>
    public int Next(string effectId, int maxExclusive) => Stream(effectId).NextInt(maxExclusive);
}
