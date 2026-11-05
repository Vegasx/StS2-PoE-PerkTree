using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MegaCrit.Sts2.Core.Combat;
using MegaCrit.Sts2.Core.Commands;
using MegaCrit.Sts2.Core.Entities.Cards;
using MegaCrit.Sts2.Core.Entities.Creatures;
using MegaCrit.Sts2.Core.Entities.Players;
using MegaCrit.Sts2.Core.GameActions.Multiplayer;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Models.Cards;
using MegaCrit.Sts2.Core.Models.Powers;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using PerkTree.Core;

// Runs the compiled host and handlers with the installed game types. Only command/animation boundaries are intercepted.
// No running game, saves, installation or player data is changed.
const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
var assembly = typeof(PerkState).Assembly;
var hostType = assembly.GetType("PerkTree.Mod.PerkHostModel", true)!;
var handlerType = assembly.GetType("PerkTree.Mod.Effects.EffectHandler", true)!;
var harmony = new Harmony("PerkTree.MinorPerkRegression");
void Patch(MethodBase method, string capture) => harmony.Patch(method, prefix: new HarmonyMethod(typeof(Capture).GetMethod(capture)!));
Patch(typeof(CombatManager).GetProperty("IsInProgress")!.GetMethod!, nameof(Capture.InCombat));
Patch(typeof(RunState).GetProperty("TotalFloor")!.GetMethod!, nameof(Capture.TotalFloor));
Patch(handlerType.GetMethod("GrantBlock", Private)!, nameof(Capture.Block));
Patch(handlerType.GetMethod("GrantPower", Private)!.MakeGenericMethod(typeof(VigorPower)), nameof(Capture.Vigor));
Patch(typeof(PlayerCmd).GetMethod("GainGold")!, nameof(Capture.Gold));
Patch(hostType.GetMethod("Roll")!, nameof(Capture.Roll));

T Empty<T>()
{
    var instance = (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    if (instance is AbstractModel model) Field(model, "<IsMutable>k__BackingField", true);
    return instance;
}
void Field(object instance, string name, object? value)
{
    var type = instance.GetType();
    while (type != null)
    {
        var field = type.GetField(name, Private);
        if (field != null) { field.SetValue(instance, value); return; }
        type = type.BaseType;
    }
    throw new Exception("Missing field " + name);
}
Player Player()
{
    var player = Empty<Player>();
    var creature = Empty<Creature>();
    Field(creature, "_currentHp", 50);
    Field(creature, "_powers", new List<PowerModel>());
    Field(creature, "<Player>k__BackingField", player);
    Field(creature, "<Side>k__BackingField", CombatSide.Player);
    Field(player, "<Creature>k__BackingField", creature);
    Field(player, "<PlayerCombatState>k__BackingField", Empty<PlayerCombatState>());
    Field(player, "_runState", Empty<RunState>());
    return player;
}
var owner = Player();
var other = Player();
var enemy = Empty<Creature>();
Field(enemy, "<Side>k__BackingField", CombatSide.Enemy);
Field(enemy, "_currentHp", 50);
var skill = Empty<DefendIronclad>(); Field(skill, "_owner", owner);
var attack = Empty<StrikeIronclad>(); Field(attack, "_owner", owner);
var foreignSkill = Empty<DefendIronclad>(); Field(foreignSkill, "_owner", other);
Field(skill, "<Type>k__BackingField", CardType.Skill);
Field(foreignSkill, "<Type>k__BackingField", CardType.Skill);
Field(attack, "<Type>k__BackingField", CardType.Attack);
var debuff = Empty<VulnerablePower>(); Field(debuff, "_owner", enemy);
var strength = Empty<StrengthPower>(); Field(strength, "_owner", enemy);
var ctx = new ThrowingPlayerChoiceContext();
var ids = new[] { "first_skill_block", "first_exhaust_vigor", "first_debuff_block", "end_turn_energy_block", "rest_heal_bonus", "shop_gold" };
var amounts = new[] { 1m, 1m, 1m, 1m, 2m, 5m };
var nodes = ids.SelectMany((id, i) => Enumerable.Range(0, 3).Select(n => new NodeDef
    { Id = id + n, Branch = "general", EffectId = id, Magnitude = amounts[i] })).ToList();
var graph = TreeGraph.Build(new TreeDef { CharacterId = "IRONCLAD", Nodes = nodes, Starts = nodes.Select(n => n.Id).ToList(),
    Branches = [new BranchDef { Id = "general", Kind = BranchKind.General }] },
    new EffectCatalog { Effects = ids.Select(id => new EffectDef { Id = id, Scope = EffectScope.Self }).ToList() });
var state = new PerkState(graph);
AbstractModel Host(PerkState perks)
{
    var host = RuntimeHelpers.GetUninitializedObject(hostType);
    hostType.GetMethod("Attach", Private)!.Invoke(host, [owner, perks]);
    return (AbstractModel)host;
}
var host = Host(state);
void Equal(decimal expected, decimal actual, string label)
{
    if (actual != expected) throw new Exception($"{label}: expected {expected}, got {actual}");
}
async Task Turn(AbstractModel model, int number)
{
    Field(owner.PlayerCombatState!, "<TurnNumber>k__BackingField", number);
    await model.BeforeSideTurnStart(ctx, CombatSide.Player, [owner.Creature], null!);
}
void Energy(int amount) => Field(owner.PlayerCombatState!, "_energy", amount);
CardPlay Play(CardModel card)
{
    var play = Empty<CardPlay>();
    Field(play, "<Card>k__BackingField", card);
    return play;
}

try
{
    state.AwardPoints(nodes.Count); state.OpenAllocationWindow();
    foreach (var node in nodes) state.TryAllocate(node.Id);
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(0, Capture.BlockTotal, "pending perks are inactive");
    state.Commit();
    await Turn(host, 1);
    await host.AfterCardPlayed(ctx, Play(foreignSkill));
    await host.AfterCardPlayed(ctx, Play(attack));
    Equal(0, Capture.BlockTotal, "foreign Skills and Attacks do not trigger Steady");
    await host.AfterCardPlayed(ctx, Play(skill));
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(3, Capture.BlockTotal, "three Steady nodes grant once");
    Equal(1, Capture.BlockCalls, "Steady combines its nodes");
    await Turn(host, 2);
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(6, Capture.BlockTotal, "Steady resets next turn");
    await host.AfterCardExhausted(ctx, foreignSkill, false);
    Equal(0, Capture.VigorTotal, "foreign exhaust ignored");
    await host.AfterCardExhausted(ctx, skill, true);
    await host.AfterCardExhausted(ctx, attack, false);
    Equal(3, Capture.VigorTotal, "Kindled includes Ethereal and only triggers once");
    await Turn(host, 3);
    await host.AfterCardExhausted(ctx, attack, false);
    Equal(6, Capture.VigorTotal, "Kindled resets next turn");

    var block = Capture.BlockTotal;
    await host.AfterPowerAmountChanged(ctx, debuff, 1, other.Creature, null);
    await host.AfterPowerAmountChanged(ctx, debuff, -1, owner.Creature, null);
    await host.AfterPowerAmountChanged(ctx, strength, 1, owner.Creature, null);
    Equal(block, Capture.BlockTotal, "Guarded ignores allies, debuff removal and buffs");
    await host.AfterPowerAmountChanged(ctx, debuff, 1, owner.Creature, null);
    await host.AfterPowerAmountChanged(ctx, debuff, 1, owner.Creature, null);
    Equal(block + 3, Capture.BlockTotal, "Guarded triggers once");
    await Turn(host, 4);
    await host.AfterPowerAmountChanged(ctx, strength, -1, owner.Creature, null);
    Equal(block + 6, Capture.BlockTotal, "negative Strength counts as a debuff");
    block = Capture.BlockTotal;
    Energy(0);
    await host.BeforeSideTurnEnd(ctx, CombatSide.Player, [owner.Creature]);
    Equal(block, Capture.BlockTotal, "Patient needs leftover Energy");
    Energy(1);
    await host.BeforeSideTurnEnd(ctx, CombatSide.Enemy, [enemy]);
    await host.BeforeSideTurnEnd(ctx, CombatSide.Player, [other.Creature]);
    Equal(block, Capture.BlockTotal, "Patient ignores enemy and teammate-only turns");
    await host.BeforeSideTurnEnd(ctx, CombatSide.Player, [owner.Creature]);
    Equal(block + 3, Capture.BlockTotal, "Patient with one Energy");
    Energy(8);
    await host.BeforeSideTurnEnd(ctx, CombatSide.Player, [owner.Creature]);
    Equal(block + 6, Capture.BlockTotal, "Patient does not scale with leftover Energy");
    Equal(8, owner.PlayerCombatState!.Energy, "Patient leaves Energy intact");
    Equal(26, host.ModifyRestSiteHealAmount(owner.Creature, 20), "Restful stacks");
    Equal(26, host.ModifyRestSiteHealAmount(owner.Creature, 20), "Restful preview is pure");
    Equal(20, host.ModifyRestSiteHealAmount(other.Creature, 20), "Restful owner filter");

    await Turn(host, 5);
    block = Capture.BlockTotal;
    Capture.FailRolls = true;
    await host.AfterCardPlayed(ctx, Play(skill));
    Capture.FailRolls = false;
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(block, Capture.BlockTotal, "a failed roll still consumes the first Skill");
    await host.BeforeSideTurnStart(ctx, CombatSide.Player, [other.Creature], null!);
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(block, Capture.BlockTotal, "another player's extra turn does not reset Steady");
    await host.BeforeCombatStart();
    await host.AfterCardPlayed(ctx, Play(skill));
    Equal(block + 3, Capture.BlockTotal, "combat start resets first-event flags");

    Capture.Floor = 7;
    await host.AfterRoomEntered(Empty<RestSiteRoom>());
    Equal(0, Capture.GoldTotal, "Provisioned ignores other rooms");
    await host.AfterRoomEntered(Empty<MerchantRoom>());
    await host.AfterRoomEntered(Empty<MerchantRoom>());
    Equal(15, Capture.GoldTotal, "Provisioned stacks and deduplicates room notifications");
    var restored = PerkState.FromSave(graph, state.ToSave(), out var report);
    Equal(0, report.RefundedPoints, "save restores allocations");
    var reloaded = Host(restored);
    await reloaded.AfterRoomEntered(Empty<MerchantRoom>());
    Equal(15, Capture.GoldTotal, "Provisioned cannot be farmed by save/reload");
    Capture.Floor = 12;
    await reloaded.AfterRoomEntered(Empty<MerchantRoom>());
    Equal(30, Capture.GoldTotal, "Provisioned pays at the next shop");
    if (!ReferenceEquals(Capture.GoldOwner, owner)) throw new Exception("Gold went to the wrong player");
    Console.WriteLine("PASS: all six compiled perks; three-node stacking, pending/owner filters, first-event limits, turn resets, negative-stat debuffs, Energy conditions, pure Rest preview, and shop save/reload deduplication.");
}
finally { harmony.UnpatchAll(harmony.Id); }

public static class Capture
{
    public static decimal BlockTotal, VigorTotal, GoldTotal;
    public static int BlockCalls, Floor;
    public static bool FailRolls;
    public static Player? GoldOwner;
    public static bool InCombat(ref bool __result) { __result = true; return false; }
    public static bool TotalFloor(ref int __result) { __result = Floor; return false; }
    public static bool Roll(ref decimal __result)
    { if (!FailRolls) return true; __result = 0; return false; }
    public static bool Block(decimal amount, ref Task __result)
    { BlockTotal += amount; BlockCalls++; __result = Task.CompletedTask; return false; }
    public static bool Vigor(decimal amount, ref Task __result)
    { VigorTotal += amount; __result = Task.CompletedTask; return false; }
    public static bool Gold(decimal __0, Player __1, ref Task __result)
    { GoldTotal += __0; GoldOwner = __1; __result = Task.CompletedTask; return false; }
}
