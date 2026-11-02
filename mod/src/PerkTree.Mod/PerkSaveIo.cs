using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.Saves.Managers;

namespace PerkTree.Mod;

/// <summary>
/// Reads, writes and deletes the perk sidecar next to the game's own run save, through the game's own save store.
///
/// Why a sidecar: the game's run save (SerializablePlayer / SerializableRun) is a fixed, source-generated schema that is
/// also sent over the network as a packet, so there is no slot for mod data inside it.
///
/// Why the game's store and path: it gives the same profile folder, the same cloud-sync handling and the same modded-save
/// separation as the run save itself. The sidecar is simply "<run save path>.perktree.json", the same trick the game uses
/// for its own backup file (its DeleteCurrentRun removes the run save plus a suffixed sibling).
///
/// Everything here is reflection onto private members of RunSaveManager / SaveManager, so it is all behind
/// <see cref="Available"/>: if a game patch renames any of them we disable persistence and say so, rather than throw.
/// </summary>
internal static class PerkSaveIo
{
    private const string Suffix = ".perktree.json";

    private static readonly FieldInfo? StoreField = AccessTools.Field(typeof(RunSaveManager), "_saveStore");
    private static readonly MethodInfo? SinglePlayerPath = AccessTools.PropertyGetter(typeof(RunSaveManager), "CurrentRunSavePath");
    private static readonly MethodInfo? MultiplayerPath = AccessTools.PropertyGetter(typeof(RunSaveManager), "CurrentMultiplayerRunSavePath");
    private static readonly FieldInfo? RunSavesField = AccessTools.Field(typeof(SaveManager), "_runSaveManager");

    public static bool Available =>
        StoreField != null && SinglePlayerPath != null && MultiplayerPath != null && RunSavesField != null;

    /// <summary>The game's run-save manager, for reading a sidecar when a run is loaded.</summary>
    public static RunSaveManager? GameRunSaves() =>
        SaveManager.Instance is { } sm ? RunSavesField?.GetValue(sm) as RunSaveManager : null;

    private static ISaveStore? Store(RunSaveManager m) => StoreField?.GetValue(m) as ISaveStore;

    private static string? PathFor(RunSaveManager m, bool multiplayer) =>
        (multiplayer ? MultiplayerPath : SinglePlayerPath)?.Invoke(m, null) is string { Length: > 0 } p ? p + Suffix : null;

    public static void Write(RunSaveManager m, bool multiplayer, string json)
    {
        if (Store(m) is { } store && PathFor(m, multiplayer) is { } path) store.WriteFile(path, json);
    }

    public static string? Read(RunSaveManager m, bool multiplayer) =>
        Store(m) is { } store && PathFor(m, multiplayer) is { } path && store.FileExists(path) ? store.ReadFile(path) : null;

    public static void Delete(RunSaveManager m, bool multiplayer)
    {
        if (Store(m) is { } store && PathFor(m, multiplayer) is { } path && store.FileExists(path)) store.DeleteFile(path);
    }
}
