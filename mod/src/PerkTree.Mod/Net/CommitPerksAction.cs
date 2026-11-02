using MegaCrit.Sts2.Core.Entities.Multiplayer;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Multiplayer.Serialization;

namespace PerkTree.Mod;

/// <summary>
/// "This player locked in these perk nodes", as one of the game's own ordered actions. The game finds mod types that
/// implement INetAction by itself (ActionTypes.Initialize scans loaded mods), so no registration is needed.
///
/// It travels the same road as a map vote: the sender asks the host, the host puts it in the action queue and tells
/// every client, and every client (the sender's included) runs it at the same position. The action is enqueued just
/// before the player's map vote, in the same per-player queue, so it always runs first and the node set is in place on
/// every machine before anyone travels or fights.
///
/// Everything a client needs has to be inside the action: the game rebuilds it from its network form on the other
/// machines, and on the sender's too, so nothing can ride along in memory.
/// </summary>
public sealed class CommitPerksAction : GameAction
{
    private readonly Player _player;

    public string[] NodeIds { get; }

    /// <summary>The sender's <see cref="PerkState.SyncHash"/> before the commit. Receivers compare it to their own copy to catch drift early.</summary>
    public ulong SyncHashBefore { get; }

    public CommitPerksAction(Player player, string[] nodeIds, ulong syncHashBefore)
    {
        _player = player;
        NodeIds = nodeIds;
        SyncHashBefore = syncHashBefore;
    }

    public override ulong OwnerId => _player.NetId;

    // The map vote uses Any (it can be requested at the boundary between combat and map), and we run in the same place.
    public override GameActionType ActionType => GameActionType.Any;

    protected override Task ExecuteAction()
    {
        PerkRuntime.ApplyCommit(_player, NodeIds, SyncHashBefore);
        return Task.CompletedTask;
    }

    public override INetAction ToNetAction() => new NetCommitPerksAction { NodeIds = NodeIds, SyncHashBefore = SyncHashBefore };

    public override string ToString() => $"CommitPerksAction(player {_player.NetId}, {NodeIds.Length} node(s))";
}

/// <summary>The wire form of <see cref="CommitPerksAction"/>. A struct with public fields, like the game's own.</summary>
public struct NetCommitPerksAction : INetAction, IPacketSerializable
{
    // 7 bits holds 0..127, comfortably above PerkState.MaxCommitNodes; anything over the real cap is refused on arrival.
    private const int CountBits = 7;
    private const int MaxOnWire = (1 << CountBits) - 1;

    public string[] NodeIds;
    public ulong SyncHashBefore;

    public GameAction ToGameAction(Player player)
        => new CommitPerksAction(player, NodeIds ?? Array.Empty<string>(), SyncHashBefore);

    public void Serialize(PacketWriter writer)
    {
        var ids = NodeIds ?? Array.Empty<string>();
        var count = Math.Min(ids.Length, MaxOnWire);
        writer.WriteInt(count, CountBits);
        for (var i = 0; i < count; i++) writer.WriteString(ids[i]);
        writer.WriteULong(SyncHashBefore, 64);
    }

    public void Deserialize(PacketReader reader)
    {
        var count = reader.ReadInt(CountBits);
        NodeIds = new string[count];
        for (var i = 0; i < count; i++) NodeIds[i] = reader.ReadString();
        SyncHashBefore = reader.ReadULong(64);
    }
}
