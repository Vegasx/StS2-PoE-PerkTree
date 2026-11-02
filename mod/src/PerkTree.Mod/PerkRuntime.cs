using MegaCrit.Sts2.Core.Context;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace PerkTree.Mod;

/// <summary>Owns the per-player perk hosts for the current run.</summary>
internal static class PerkRuntime
{
    // Which run the cache belongs to. Comparing the RunState reference means a new, abandoned or reloaded run
    // starts clean without us needing to catch the exact moment each of those happens.
    private static RunState? _run;
    private static readonly Dictionary<ulong, PerkHostModel> ByPlayer = new();
    private static List<AbstractModel> _hostList = new();

    // Set by the RunState.FromSerializable patch: the one RunState that came from a save file, and that save's start time.
    // Only that run restores from the sidecar; any other RunState is a brand-new run and starts fresh.
    private static RunState? _loadedRun;
    private static long _loadedRunStartTime;

    private static readonly MethodInfo? RunStateGetter = AccessTools.PropertyGetter(typeof(RunManager), "State");

    /// <summary>
    /// The delegate given to ModHelper.SubscribeForRunStateHooks. Runs on every hook iteration, so it must stay a
    /// cheap lookup once the hosts for the run exist.
    /// </summary>
    public static IEnumerable<AbstractModel> HostsFor(RunState run)
    {
        if (!ReferenceEquals(run, _run))
        {
            _run = run;
            ByPlayer.Clear();
            _hostList = new List<AbstractModel>();
            var saved = ReferenceEquals(run, _loadedRun) ? ReadSidecar(_loadedRunStartTime) : null;
            foreach (var player in run.Players)
            {
                var state = PerkData.CreateState(player, saved?.For(player.NetId, player.Character.Id.Entry));
                if (state is null) continue;
                var host = new PerkHostModel(player, state);
                ByPlayer[player.NetId] = host;
                _hostList.Add(host);
            }
        }
        return _hostList;
    }

    // ------------------------------------------------------------------ persistence

    public static void NoteLoadedRun(RunState run, long startTime)
    {
        _loadedRun = run;
        _loadedRunStartTime = startTime;
    }

    /// <summary>
    /// Called from inside the game's own run save. Persistence must never be able to break saving, so this swallows and
    /// logs everything.
    /// </summary>
    public static void OnRunSaved(RunSaveManager saves, long runStartTime, bool multiplayer)
    {
        try
        {
            if (!PerkSaveIo.Available) return;
            var run = CurrentRunState();
            if (run is null) return;

            // Build (and, for a loaded run, RESTORE) the hosts before writing. The game resaves immediately after a load to
            // bump its reload counter, possibly before any hook has made us create the hosts; writing from an empty runtime
            // then would overwrite the sidecar we are about to read.
            HostsFor(run);
            if (ByPlayer.Count == 0) return;

            var save = PerkRunSave.Capture(runStartTime,
                ByPlayer.Values.Select(h => (h.Owner.NetId, h.Owner.Character.Id.Entry, h.State)));
            PerkSaveIo.Write(saves, multiplayer, save.ToJson());
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not save perks: {e.GetType().Name}: {e.Message}");
        }
    }

    public static void OnRunDeleted(RunSaveManager saves, bool multiplayer)
    {
        try { if (PerkSaveIo.Available) PerkSaveIo.Delete(saves, multiplayer); }
        catch (Exception e) { MainFile.LogWarn($"could not delete the perk sidecar: {e.Message}"); }
    }

    private static PerkRunSave? ReadSidecar(long runStartTime)
    {
        try
        {
            if (!PerkSaveIo.Available || PerkSaveIo.GameRunSaves() is not { } saves) return null;
            // We don't know here whether the loaded run was single- or multiplayer, so try both files. The start time
            // is what actually identifies the right one.
            foreach (var multiplayer in new[] { false, true })
                if (PerkRunSave.TryParse(PerkSaveIo.Read(saves, multiplayer), out var save) && save!.RunStartTime == runStartTime)
                    return save;
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not read the perk sidecar: {e.GetType().Name}: {e.Message}");
        }
        MainFile.Log("no matching perk sidecar for this run; starting with no perks");
        return null;
    }

    /// <summary>The RunState the game is currently running. RunManager.State is private, hence reflection.</summary>
    private static RunState? CurrentRunState() =>
        RunManager.Instance is { } manager ? RunStateGetter?.Invoke(manager, null) as RunState : null;

    // ------------------------------------------------------------------ lookups

    public static PerkHostModel? HostOf(Player? player) =>
        player is not null && ByPlayer.TryGetValue(player.NetId, out var h) ? h : null;

    /// <summary>The map became interactive: players who haven't voted yet may spend points.</summary>
    public static void OnMapTravelEnabled()
    {
        foreach (var host in ByPlayer.Values) host.OpenWindow();
    }

    /// <summary>The local player cast their first vote. Other players' commits arrive as actions, never through here.</summary>
    public static void OnLocalPlayerVoted() => LocalHost()?.LockForVote();

    /// <summary>
    /// Runs on EVERY client when a player's CommitPerksAction comes up in the action queue. It must depend only on synced
    /// state, so every machine reaches the same result (see PerkState.TryCommitExact). A mismatch of the sender's
    /// state hash is logged loudly: it means the clients had already drifted before this point.
    /// </summary>
    public static void ApplyCommit(Player player, IReadOnlyList<string> nodes, ulong senderSyncHash)
    {
        try
        {
            if (CurrentRunState() is { } run) HostsFor(run); // make sure hosts exist, e.g. right after a load
            var host = HostOf(player);
            if (host is null)
            {
                MainFile.LogWarn($"commit from player {player.NetId}, who has no perk tree here; ignored");
                return;
            }

            var mine = host.State.SyncHash();
            if (mine != senderSyncHash)
                MainFile.LogError($"PERK STATE MISMATCH for player {player.NetId}: sender {senderSyncHash:X16}, this client {mine:X16}. " +
                                  "The clients disagreed about this player's perks or counters before this lock.");

            var result = host.State.TryCommitExact(nodes);
            if (result == PerkCommitResult.Ok)
                MainFile.Log($"{player.NetId}: locked {nodes.Count} node(s) ({host.State.UnspentPoints} points left, hash {host.State.SyncHash():X16})");
            else
                MainFile.LogError($"{player.NetId}: commit refused ({result}); no nodes locked. Every client refuses the same way.");
        }
        catch (Exception e)
        {
            MainFile.LogError($"applying a perk commit failed: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// The active total of an effect for whoever owns this creature, or 0 for enemies, pets and players without
    /// a perk tree. Called from damage/block calculation, so it stays two dictionary lookups.
    /// </summary>
    public static decimal ResistFor(Creature? creature, string effectId) =>
        HostOf(creature?.Player)?.Effective(effectId) ?? 0m;

    /// <summary>The local player's host, for the debug console and (later) the tree UI.</summary>
    public static PerkHostModel? LocalHost() =>
        ByPlayer.Values.FirstOrDefault(h => LocalContext.IsMe(h.Owner));
}
