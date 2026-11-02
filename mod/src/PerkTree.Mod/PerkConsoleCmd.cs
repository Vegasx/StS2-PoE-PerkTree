using System.Text;
using HarmonyLib;
using MegaCrit.Sts2.Core.DevConsole;
using MegaCrit.Sts2.Core.DevConsole.ConsoleCommands;
using MegaCrit.Sts2.Core.Entities.Players;

namespace PerkTree.Mod;

/// <summary>
/// Dev tool from design doc section 10, standing in for the tree UI until it exists:
///   perk show | grant N | open | alloc ID | refund ID | commit
/// Local only, except `commit`, which goes through the real lock action. Don't use grant/alloc in multiplayer: they
/// change this client's state alone.
/// </summary>
internal sealed class PerkConsoleCmd : AbstractConsoleCmd
{
    public override string CmdName => "perk";
    public override string Args => "<show|grant N|open|alloc ID|refund ID|commit>";
    public override string Description => "Perk Tree debug: inspect and edit the local player's perks.";
    public override bool IsNetworked => false;

    public override CmdResult Process(Player? player, string[] args)
    {
        // Unverified whether the game includes the command name in args, so accept both shapes.
        if (args.Length > 0 && args[0].Equals(CmdName, StringComparison.OrdinalIgnoreCase)) args = args[1..];

        var host = PerkRuntime.LocalHost();
        if (host is null) return new CmdResult(false, "No perk tree for the local player (no run, or no tree file for this character).");
        var state = host.State;
        var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "show";

        switch (verb)
        {
            case "grant" when args.Length > 1 && int.TryParse(args[1], out var n) && n >= 0:
                state.AwardPoints(n);
                return new CmdResult(true, $"+{n} points ({state.UnspentPoints} unspent).");
            case "open":
                state.OpenAllocationWindow();
                return new CmdResult(true, "Allocation window opened.");
            case "alloc" when args.Length > 1:
                return Report("alloc", args[1], state.TryAllocate(args[1]));
            case "refund" when args.Length > 1:
                return Report("refund", args[1], state.TryRefundPending(args[1]));
            case "commit":
                // Same path as a real map vote, so the console exercises the ordered action too.
                host.LockForVote();
                return new CmdResult(true, "Lock requested; nodes commit when the action runs. Check the log.");
            case "show":
                return new CmdResult(true, Describe(state));
            default:
                return new CmdResult(false, $"Usage: {CmdName} {Args}");
        }
    }

    private static CmdResult Report(string what, string id, PerkOpResult result) =>
        new(result == PerkOpResult.Ok, result == PerkOpResult.Ok ? $"{what} {id}: ok" : $"{what} {id}: {result}");

    private static string Describe(PerkState s)
    {
        var sb = new StringBuilder();
        sb.Append($"unspent {s.UnspentPoints}, window {(s.AllocationOpen ? "open" : "closed")}; ");
        sb.Append($"committed [{string.Join(", ", s.Committed)}]; pending [{string.Join(", ", s.Pending)}]");
        foreach (var t in s.GetTotals(includePending: true).Values)
            sb.Append($"\n  {t.EffectId}: {t.Effective}{(t.Cap is { } c ? $" / cap {c}" : "")}{(t.AtCap ? " (max)" : "")}");
        return sb.ToString();
    }
}

/// <summary>
/// The game builds its command list inside DevConsole's constructor with a private RegisterCommand, so we add ours
/// right after it. Reflection because the method is private.
/// </summary>
[HarmonyPatch(typeof(DevConsole), MethodType.Constructor, new[] { typeof(bool) })]
internal static class DevConsoleRegisterPatch
{
    private static void Postfix(DevConsole __instance)
    {
        try
        {
            AccessTools.Method(typeof(DevConsole), "RegisterCommand")
                ?.Invoke(__instance, new object[] { new PerkConsoleCmd() });
        }
        catch (Exception e)
        {
            MainFile.LogWarn($"could not register the perk console command: {e.Message}");
        }
    }
}
