using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Runs;

namespace PerkTree.Mod;

/// <summary>Sending the local player's lock into the game's action queue.</summary>
internal static class PerkNet
{
    /// <summary>
    /// Enqueues the local player's commit. In multiplayer the commit is applied when the ordered action comes back round
    /// (to every client, the sender included), not here. If enqueuing fails in SINGLE-player there is nobody to be out of
    /// step with, so the commit is applied directly; in multiplayer that would desync, so it only logs.
    /// </summary>
    public static void RequestCommit(PerkHostModel host)
    {
        var nodes = host.State.Pending.ToArray();
        var hash = host.State.SyncHash();
        try
        {
            var sync = RunManager.Instance?.ActionQueueSynchronizer
                       ?? throw new InvalidOperationException("no action queue synchronizer (no run in progress?)");
            sync.RequestEnqueue(new CommitPerksAction(host.Owner, nodes, hash));
            MainFile.Log($"{host.Owner.NetId}: lock requested ({nodes.Length} node(s))");
        }
        catch (Exception e)
        {
            MainFile.LogError($"could not enqueue the perk commit: {e.GetType().Name}: {e.Message}");
            if (!IsMultiplayer()) PerkRuntime.ApplyCommit(host.Owner, nodes, hash);
        }
    }

    private static bool IsMultiplayer()
        => RunManager.Instance?.NetService?.Type is NetGameType.Host or NetGameType.Client;
}
