using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;

namespace PerkTree.Mod;

/// <summary>Mod entry point, found by the game through [ModInitializer].</summary>
[ModInitializer(nameof(Initialize))]
public class MainFile
{
    public const string ModId = "PerkTree";

    public static void Initialize()
    {
        // With [ModInitializer] present the game does NOT auto-patch (per its own docs), so we must call PatchAll.
        new Harmony(ModId).PatchAll(typeof(MainFile).Assembly);

        // The officially supported way to get hooks without owning a relic or a power: hand the game our own
        // models and it will call them whenever it iterates hook listeners for the run.
        ModHelper.SubscribeForRunStateHooks(ModId, PerkRuntime.HostsFor);
        // Combat-only hooks (card played, turn start, orb channeled...) iterate the COMBAT state's listeners, which never include
        // run subscribers (checked in the IL: Hook.AfterCardPlayed uses ICombatState.IterateHookListeners). Without this the
        // perks were bought but never fired. A run iteration that also walks the combat can now see a host twice; the
        // RunState.IterateHookListeners postfix (HookDedupePatch) removes that second copy.
        ModHelper.SubscribeForCombatStateHooks(ModId, _ => PerkRuntime.CurrentHosts);

        Log("loaded");
    }

    public static void Log(string message) => GD.Print($"[{ModId}] {message}");
    public static void LogWarn(string message) => GD.PushWarning($"[{ModId}] {message}");
    public static void LogError(string message) => GD.PushError($"[{ModId}] {message}");
}
