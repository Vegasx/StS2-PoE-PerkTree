# sts2-perk-tree (the mod's code)

> **Where this lives:** this is the `mod/` folder of the StS2-PoE-PerkTree repo. The perk list, tree layouts and the editor are
> in the repo's `data/`, `editor/` and `tools/` folders (see the README at the top of the repo). The mod's code is here.
> Paths below are relative to this folder. The data format there differs from the sample data used here; reading it
> from the mod is the next integration step (see `docs/open-decisions.md`).

A Slay the Spire 2 mod that replaces Relic Forge's "imbue a relic" prefixes with a **PoE2-style perk tree**:
earn points during a run, spend them on tree nodes, each node grants one prefix effect for the rest of the run.

Design doc: `perk-tree-design.md` in the *Relic Forge* Claude project (v1.4). This repo implements it in the
build order from section 13.

## Status

| Build step | State |
|---|---|
| 1. Audit prefix list | **done**: 148 old prefixes -> 96 tree effects, see `docs/prefix-audit.md`; your four decisions are applied |
| 2. Native UI reconnaissance | **part 1 done** from `sts2.dll` metadata + the game's `sts2.xml` docs, see `docs/native-recon.md`; scene/theme inventory still open |
| 3. Effect host | **first slice written** in `PerkTree.Mod` (compiles against the real game DLLs; not yet run in game) |
| 4. Tree data format + validator | **done**, in `PerkTree.Core` |
| 5. Allocation flow (points, pending/commit, lock, save/load) | **done** (not yet run in game): logic in `PerkTree.Core`; map hooks, debug console command and persistence in `PerkTree.Mod` |
| 6. UI | **slice 1 built, never run in game**: a "Perks" button on the map screen opens a summary panel (`PerkUi.cs`). Tree screen not started |
| 7. Co-op sync | **written, not yet run in game**: the lock is an ordered game action (`Net/CommitPerksAction.cs`), see "Co-op" below |
| 8. Remaining characters | not started |

## Layout

```
src/PerkTree.Core   engine-independent rules: data model, validator, PerkState. No game/Godot references.
src/PerkTree.Mod    the game-facing mod: host model, map-vote lock patches, debug console command
src/PerkTree.Cli    `perktree validate` for build/CI
tests/              xunit tests for Core
data/sample/        small hand-written tree + effect catalog: documents the JSON format, used by the unit tests
data/runtime/       what the mod ships: exported from the editor with `node tools/export-mod.mjs` (repo root), copied next to the dll
data/native-keywords.txt   every native keyword/power id, generated from sts2.dll (feeds the validator)
data/audit/         audit output: audit.json (every old prefix + disposition) and effects.draft.json (the 96-effect catalog)
tools/audit/        build_audit.py, regenerates the audit from the old repo's PrefixTable.cs
docs/prefix-audit.md       the audit summary, findings and decisions made
docs/open-decisions.md     what is still undecided, assumptions made, and what has never run in game
docs/native-recon.md       what the game's own systems give us (loader, hooks, map vote, tooltips)
```

Core deliberately knows nothing about STS2 so it can be unit tested without the game. The future mod project
(game-coupled) will sit on top of it, with every reference to a native scene or class kept in one adapter file
(design doc 9.8).

## Using Core from the mod

```csharp
var graph = TreeGraph.Build(PerkJson.LoadTreeFile(treePath), PerkJson.LoadCatalogFile(effectsPath), knownKeywords);
var perks = new PerkState(graph);

perks.AwardForCombat(RoomKind.Elite);      // after a combat win: 1 / 2 / 3 points
perks.OpenAllocationWindow();              // map screen shown
perks.TryAllocate("poison_2");             // pending, freely refundable via TryRefundPending
perks.Commit();                            // player voted for the next node: pending -> permanent, window closes

perks.GetTotal("some.effect");             // gameplay value (committed only), clamped to the effect's cap
perks.GetTotal("some.effect", includePending: true); // tree UI
perks.PreviewAllocate("poison_3");         // (before, after) for "25% -> 50%" hover text
perks.ToSave() / PerkState.FromSave(...);  // pending nodes survive quit-mid-allocation
perks.CommittedHash();                     // compare across co-op peers to detect divergence
```

## Building the mod

Needs the game's DLLs (`sts2.dll`, `GodotSharp.dll`, `0Harmony.dll`). They are gitignored, never commit them. Point the
build at your install:

```
dotnet build src/PerkTree.Mod -c Release -p:Sts2LibDir="C:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64"
```

Then copy `PerkTree.dll`, `PerkTree.json` and the `data/` folder from `src/PerkTree.Mod/bin/Release/net9.0/` into
`<game folder>\mods\PerkTree\`. The game targets .NET 9, so build with the .NET 9 (or newer) SDK. (`-p:ModTfm=net10.0`
exists only for machines without the net9.0 targeting pack, where it verifies the code compiles; don't ship that build.)

Debug console command (until the tree UI exists), in a solo run: `perk show | grant N | open | alloc ID | refund ID | commit`.
It needs a tree file for your character; only `silent` ships as placeholder data right now.

## Build and test

```
dotnet build
dotnet test
dotnet run --project src/PerkTree.Cli -- validate data/sample/effects.sample.json data/sample/tree.sample.json
dotnet run --project src/PerkTree.Cli -- validate-catalog data/audit/effects.draft.json --keywords data/native-keywords.txt
```

## Ground rules

- **Balance is the owner's call.** Numbers in the audit are the old prefix numbers, not rebalanced.
- **Performance:** Harmony patches must be cheap and exit immediately when the player has no relevant perk. No patch on a per-frame
  method (`Update`, `_Process`, ...) without asking first. Every patch site today fires on a discrete game event.
- **Chance effects stack past 100%.** Each perk of a chance effect rolls on its own (three +50% perks = three rolls at 50%); a chance past 100% (one big perk, or `proc_boost` / `proc_double`) gives its whole part as sure procs plus a roll for the rest. Only on/off effects and resistances keep a cap. Procs per event are limited to 100 so nothing can loop forever.
- **Successor to Relic Forge, no relics.** The imbue system is gone: this mod adds, grants and changes no relics, and perks are the only route for these effects. Don't run it alongside Relic Forge.
- **Branch names are internal.** Branches have an id and a name in the data, but the tree UI shows no branch titles.
- **Co-op determinism** (the game's sync is strict; one misaligned client can disconnect the whole party):
  - Every random roll comes from the game's own seeded RNG through `Roll()` (`Effects/PerkRng.cs`). Never `System.Random`, `Rng.Chaotic`, the clock or a Guid.
  - Counters change only inside game hooks, so every client counts the same events and holds the same value. They are never sent.
  - Work is awaited in order with the hook's own context, never fire-and-forget.
  - `python3 tools/check_handlers.py` scans effect code for the forbidden APIs and fails if it finds one.
- **Keystone tier:** the gamble effects with a built-in cost (`energy_for_hp`, `energy_for_max_hp`, `replay_grant`, `gold_armor`,
  `vigor_proc_double`) and `doom_defer` (Undying, placed about 30 points in).

## Persistence

Perk state is saved to a sidecar file next to the game's own run save (`current_run.save.perktree.json`, or `current_run_mp.save.perktree.json`),
written through the game's save store from inside the game's own `SaveRun`, so the two always describe the same moment. It records the run's
start time and every player's committed, pending and unspent state, and is only applied to the run it was written for. The game saves at the end of
combat after the victory hook has run, so points won in a fight are in that save, and reloading resumes the won room instead of replaying it.

- **Run-long counters** (attacks played, turns taken, for `energy_per_attacks` and `energy_every_n_turns`) are saved here too, so they carry across
  combats and reloads. `PerkState.SyncHash()` covers the committed nodes AND the counters, ready for co-op peers to compare.
- **Dice are not saved.** Each combat reseeds its streams from the run seed, the player's slot, the effect and the floor, so there is no RNG state to
  persist or drift (see `docs/native-recon.md` section 9).
- In multiplayer each client's sidecar holds every player's committed nodes (they all arrive as actions); only the local player's *pending* nodes are local-only.
- A missing, corrupt or foreign sidecar just means "no saved perks"; it can never stop a run loading or a save completing.
- If the tree data changed since the save, removed or disconnected nodes are dropped and refunded one point each.
- Allocations made after the last game save revert on quit (you keep the points); see `docs/open-decisions.md` item 12.
- The sidecar is removed when the game deletes the run save (run ended or abandoned).

## Co-op

Earning points is already symmetric: the victory hook and room type are synced, so every client awards every player the same points.
Spending is not, so it is sent:

1. **Allocation is local.** A player edits their own pending nodes on their own map screen.
2. **The lock is an ordered game action.** On a player's first map click, a prefix on `NMapScreen.OnMapPointSelectedLocally` closes their window and
   enqueues a `CommitPerksAction` carrying their node list and their `SyncHash`. It goes in *before* the game's own vote action, in the same per-player
   queue, so it runs first. The host sequences it and every client, the sender's included, runs it at the same queue position and run location.
3. **Every client applies it with the same function.** `PerkState.TryCommitExact` refunds any local pending nodes, then checks the list against the
   synced points, committed set and tree (order matters, no duplicates, connected, affordable) and commits all of it or none. It ignores
   `AllocationOpen`, which follows each client's own screen timing. A refusal is identical everywhere.
4. **Drift is reported.** Each commit carries the sender's `SyncHash` (committed nodes + counters); a client whose copy differs logs
   `PERK STATE MISMATCH` with both hashes.

Also: every handler checks the event belongs to its owner, counters change only inside hooks, and all randomness uses the game's seeded RNG (see Ground rules).
The multiplayer sidecar now holds everyone's committed nodes on every client, because every client applies every player's commit.

## What the mod can do (and what is left)

Every effect is a small class in `PerkTree.Mod/Effects/` that the host calls from a game hook, only for its owner and only while its committed total is above zero.

| Group | Effects | How |
|---|---|---|
| Combat start | thorns, block, strength, dexterity, artifact, buffer, blur, regen, vigor, heal; vulnerable on enemies; strength per 300 gold; block per relic | native relic idioms (Bronze Scales, Anchor, Vajra, Akabeko, Blood Vial, Bag of Marbles) |
| Turn and draw | ping damage, energy every 4th turn (run-long count), vigor, strength from turn 5, extra draw on turn 1 / while at half HP, block at end of turn with none | turn hooks, `ModifyHandDraw` |
| Card counts | energy per 12 attacks (run-long), per-4-Attack/Skill triggers each turn, first Power draws, Power energy, a chance to draw on Skills | `AfterCardPlayed` |
| Damage taken / dealt | draw or Strength on first hit, block at half HP, gold / block per kill, energy chance on a hit, execute bonus (+damage to enemies at 20% HP) | `AfterDamageReceived`, `AfterDamageGiven`, `AfterCurrentHpChanged`, `ModifyDamageMultiplicative` |
| Potions | block, strength, dexterity, vulnerable, weak, buffer chance, plus potion_boost / potion_double | `AfterPotionUsed` |
| Vigor | six "each Vigor spent" chances, gain amplify, turn-end conversions to Block or Strength | `AfterPowerAmountChanged` on `VigorPower`, `BeforeSideTurnEnd` |
| Gambles and debuffs | symmetric Vulnerable / Weak (you and one enemy), random debuff each turn, debuffs spread when you kill a Vulnerable enemy | `BeforeSideTurnStart`, turn start, `AfterDamageGiven` |
| Card piles | block on exhaust, draw on discard, Strength/Dexterity on drawing a Curse, random Retain at turn end, Cursebound (package scaled by Curses in the deck) | `AfterCardExhausted`, `AfterCardDiscarded`, `AfterCardDrawn`, `BeforeFlushLate` |
| Power reactions | an extra Vulnerable / Poison chance, extra Doom, first Focus bonus, Strength/Dexterity loss inverted | `AfterPowerAmountChanged` (powers you put on enemies), `TryModifyPowerAmountReceived` |
| Harmony patches | exhaust dodge and Ethereal save (swap the exhaust for a discard), Discharging (damage when you gain Energy), Undying (Doom spares you once) | `CardCmd.Exhaust`, `PlayerCmd.GainEnergy`, `DoomPower.DoomKill` |
| Meta | proc_boost, proc_double, proc_reroll change every dice roll; start_power_boost; gain_amplify (Strength/Dexterity) | one funnel (`PerkHostModel.Proc`), not per-effect code |
| Cost keystones | Immolating (+Energy, lose HP each combat), Overclocked (+Energy, lose Max HP each combat), Echoing (Replay on a random hand card), Eroding (strip an enemy power each turn), Bribing (enemy hits deal less, cost Gold) | turn hooks, `OstyCmd`-style native commands, `ModifyDamageAdditive` |
| Character mechanics | Shiv duplicate / return; Orb channel chance, Focus while slots are full, Orbs at combat start; Osty summon each turn, Block on summon, Summon from HP lost, Block from Osty HP lost; Stars each turn, on Forge, refund, bonus; draw on Forge | `Shiv.CreateInHand`, `OrbCmd`, `OstyCmd.Summon`, `PlayerCmd.GainStars`, `AfterOrbChanneled`, `AfterSummon`, `AfterForge`, `AfterStars*` |
| Resistance | Vulnerable / Weak / Frail resist | Harmony postfixes |

**Every one of the 99 audited effects now has code, and so do the 3 added since (Healing, Focal, Supply).** None of the character mechanics, keystones or added perks has been run in the game yet; the checklist is in `docs/open-decisions.md` ("Never run in game").

**Useful for the UI:** `PerkState.GetTotals(includePending: true)` already gives the before/after numbers the tree needs, and `EffectTotal.NodeMagnitudes` lists each perk's own chance, so a tooltip can say "3 rolls at 50%"; run-long counters (`PerkState.Counters`) can show progress ("7 of 12 attacks"); `Effects/` classes are the one place to read what an effect really does, and the catalog's `unit` text is what the tooltip should say.

## Resistance perks (Gamble branch)

`vulnerable_resist`, `weak_resist` and `frail_resist` make each debuff N points less effective on you (0.10 = 10 points, so Vulnerable
1.5x becomes 1.4x). They are implemented in `PerkTree.Mod/ResistPatches.cs` as Harmony postfixes, because the game hardcodes its own
Vulnerable/Weak modifiers (Paper Phrog, Cruelty, Debilitate) by name. The postfix adjusts the game's final multiplier, so those still
count: resistance is applied last, floors at neutral, and a rare amplifier like Debilitate can still bite a fully resistant player.
Base values (read from the game's IL): Vulnerable 1.5, Weak 0.75, Frail 0.75. The math lives in `Core/ResistMath.cs` and is unit tested.
The sample tree has the three nodes at `gen_4`..`gen_6` so they can be tried in game.

## Known gaps in the mod slice (not yet run in game)

- **Almost no UI.** The map screen has a "Perks" button with a summary panel (points, window state, totals). Allocation is still the `perk` console command; the tree screen is the next UI slice.
- **Effect handlers: all 103 effects have code** (the 99 audited ones plus 4 added later, listed in `data/audit/added-effects.json`; 94 handler classes in `PerkTree.Mod/Effects/`, plus 3 resistance patches and 6 passive modifiers that other handlers read). None of the newest ones has been run in the game. See "What the mod can do" below.
- **Co-op has never been run with two real players.** The pieces are written and each one is checked against the real game DLL (see "Co-op"), but only a two-player session can prove the round trip. `grant`, `alloc` and `open` in the debug console change this client only, so don't use them in multiplayer.
- Harmony patch targets are checked against the real game assembly (they resolve and their injected parameters fit),
  but Harmony 2.4.2 can't run on the .NET 10 used for checking, so the patches have never actually been applied.

## Decisions made while implementing (not in the design doc, easy to change)

- **Start nodes cost a point** and grant their effect, like any other node. They are only special in that they
  need no allocated neighbor. (Doc says "defined starting node(s)" without saying whether they're free.)
- **Every node costs 1 point** (`PointRules.NodeCost`), which the 35-45 point budget assumes.
- **Magnitudes are `decimal`**, so three +10% perks are exactly 30%.
- **Effects must declare a scope** (`self` / `enemyThroughMe` / `sharedEnemy`); the validator errors on a missing
  one so a shared enemy debuff can't be silently treated as self-only.
- **Unspent points carry over** to the next map screen (design doc open question 1, assumed yes).
- **Pending refunds can't strand nodes**: refunding a pending node is refused if another allocated node would
  lose its connection to a start.
- **Loading a save against a changed tree** drops removed or cut-off nodes and refunds one point each.
- The validator reports a **warning** (not error) when a capped effect's whole-tree total exceeds its cap.
