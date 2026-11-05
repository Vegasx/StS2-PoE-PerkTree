using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Orbs;
using MegaCrit.Sts2.Core.Random;

// Exercise the actual compiled perk helper with the installed game's model factory and seeded RNG.
// Intercept only Channel's engine/animation boundary. No game process, run, save or player state is used.
var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
ModelDb.Init([typeof(LightningOrb), typeof(FrostOrb), typeof(DarkOrb), typeof(PlasmaOrb), typeof(GlassOrb)]);
var hostType = assembly.GetType("PerkTree.Mod.PerkHostModel", true)!;
var rngType = assembly.GetType("PerkTree.Mod.Effects.PerkRng", true)!;
var handlerType = assembly.GetType("PerkTree.Mod.Effects.StartChannelOrb", true)!;
var baseType = handlerType.BaseType!;
var handler = Activator.CreateInstance(handlerType, nonPublic: true)!;
var host = RuntimeHelpers.GetUninitializedObject(hostType);
var owner = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
var rng = Activator.CreateInstance(rngType, owner)!;
var streams = (Dictionary<string, Rng>)rngType.GetField("_streams", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rng)!;
streams["start_channel_orb"] = new Rng(12345UL);
hostType.GetProperty("Owner")!.SetValue(host, owner);
hostType.GetProperty("Rolls")!.SetValue(host, rng);
baseType.GetProperty("Host", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(handler, host);
var method = baseType.GetMethod("ChannelRandomOrb", BindingFlags.Instance | BindingFlags.NonPublic)!;
var channel = typeof(OrbCmd).GetMethod("Channel", [typeof(PlayerChoiceContext), typeof(OrbModel), typeof(Player)])!;
var harmony = new Harmony("PerkTree.OrbChannelRegression");
harmony.Patch(channel, prefix: new HarmonyMethod(typeof(Capture).GetMethod(nameof(Capture.Channel))!));

var expected = new Rng(12345UL);
var seen = new HashSet<OrbModel>(ReferenceEqualityComparer.Instance);
var types = new HashSet<Type>();
try
{
    for (var i = 0; i < 200; i++)
    {
        var canonical = OrbModel.GetRandomOrb(expected);
        await (Task)method.Invoke(handler, [new ThrowingPlayerChoiceContext()])!;
        var actual = Capture.Last ?? throw new Exception("Channel was not called");
        if (!actual.IsMutable || canonical.IsMutable) throw new Exception("Incorrect model mutability");
        if (actual.GetType() != canonical.GetType() || actual.Id != canonical.Id)
            throw new Exception("Random selection changed");
        if (ReferenceEquals(actual, canonical) || !seen.Add(actual)) throw new Exception("Orb instance reused");
        if (!ReferenceEquals(Capture.Owner, owner)) throw new Exception("Wrong player");
        types.Add(actual.GetType());
    }
    if (types.Count != 5) throw new Exception($"Only covered {types.Count} orb types");
    if (streams["start_channel_orb"].NextInt(1000000) != expected.NextInt(1000000))
        throw new Exception("Random stream consumption changed");
    Console.WriteLine("PASS: 200 fresh mutable orbs, all five native types, canonical templates unchanged, owner preserved, seeded sequence and RNG consumption unchanged.");
}
finally { harmony.UnpatchAll(harmony.Id); }

public static class Capture
{
    public static OrbModel? Last;
    public static Player? Owner;
    public static bool Channel(OrbModel orb, Player player, ref Task __result)
    {
        orb.AssertMutable(); // The same boundary check that killed the turn loop in the bug report.
        Last = orb;
        Owner = player;
        __result = Task.CompletedTask;
        return false;
    }
}
