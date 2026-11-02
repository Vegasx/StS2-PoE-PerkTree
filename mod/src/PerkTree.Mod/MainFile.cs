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

        Log("loaded");
    }

    public static void Log(string message) => GD.Print($"[{ModId}] {message}");
    public static void LogWarn(string message) => GD.PushWarning($"[{ModId}] {message}");
    public static void LogError(string message) => GD.PushError($"[{ModId}] {message}");
}
