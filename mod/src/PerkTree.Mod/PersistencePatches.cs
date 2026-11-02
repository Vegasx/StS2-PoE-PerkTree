using HarmonyLib;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace PerkTree.Mod;

// Persistence hooks. All four fire on discrete save/load/delete events (a few times per room), never per frame.
//
// Perk state is written at the exact moment the game writes its own run save, from inside the same call, so the two always
// describe the same instant. The game saves at the end of combat AFTER AfterCombatVictory has run (verified in its IL),
// so points awarded for a win are already in that snapshot, and reloading resumes the already-won room instead of
// replaying it. Writing the sidecar at any other moment could let a reload re-award points, so we do not.

/// <summary>The one place the game actually writes a run save (called by SaveRun(room) and by the reload-count resave).</summary>
[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.SaveRun), new[] { typeof(SerializableRun), typeof(bool) })]
internal static class RunSavedPatch
{
    // __0 = the SerializableRun being written ("save"), __1 = isMultiplayer.
    private static void Postfix(RunSaveManager __instance, SerializableRun __0, bool __1) =>
        PerkRuntime.OnRunSaved(__instance, __0.StartTime, __1);
}

/// <summary>Remembers which RunState came from a save file, so its perks are restored from the sidecar instead of starting fresh.</summary>
[HarmonyPatch(typeof(RunState), nameof(RunState.FromSerializable))]
internal static class RunLoadedPatch
{
    private static void Postfix(SerializableRun __0, RunState __result)
    {
        if (__result != null) PerkRuntime.NoteLoadedRun(__result, __0.StartTime);
    }
}

// The game removes its run save when a run ends or is abandoned; remove the sidecar with it so no stale file lingers.
// (Even if one did, it would be ignored: it carries the run's start time and is only applied to the matching run.)

[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.DeleteCurrentRun))]
internal static class SinglePlayerRunDeletedPatch
{
    private static void Postfix(RunSaveManager __instance) => PerkRuntime.OnRunDeleted(__instance, multiplayer: false);
}

[HarmonyPatch(typeof(RunSaveManager), nameof(RunSaveManager.DeleteCurrentMultiplayerRun))]
internal static class MultiplayerRunDeletedPatch
{
    private static void Postfix(RunSaveManager __instance) => PerkRuntime.OnRunDeleted(__instance, multiplayer: true);
}
