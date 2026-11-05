# Combat-start stall investigation — 2026-11-04

## Finding

The user's `BugReport-2026_11_04_16_23_31.zip` contains three combat-start turn-loop failures in
`logs/godot.log` (lines 2032, 2371, 2704). Each reports:

```
CanonicalModelException: Canonical model of type MegaCrit.Sts2.Core.Models.Orbs.GlassOrb used in incorrect place.
  AbstractModel.AssertMutable
  OrbCmd.Channel
  StartChannelOrb.OnBeforeTurnStart
  PerkHostModel.BeforeSideTurnStart
  CombatManager.StartCombatInternal
```

The game explicitly reports that the combat is stuck until the room is restarted. The failure occurs before
the first hand is dealt. This is the Preheated/random-orb perk, rather than the Max Health reconciliation.

The report's perk sidecar includes `start_channel_orb`, two Max Health nodes, and `max_hp.granted: 2`.
The Max Health reconciliation records that counter only after the native gain command completes. The logs
show no exception in `ReconcileMaxHp`, `CreatureCmd.GainMaxHp`, or `CreatureCmd.LoseMaxHp` for these stalls.
This identifies the reported failure; it is not an exhaustive proof that Max Health can never have another bug.

## Cause and fix

The installed game's `OrbModel.GetRandomOrb(rng)` returns a canonical `ModelDb` template. The non-generic
`OrbCmd.Channel(context, orb, owner)` requires a mutable instance and calls `AssertMutable`. The mod's shared
`EffectHandler.ChannelRandomOrb` passed the template directly.

The helper now calls `.ToMutable()` before channeling, matching the game's generic channeling path.
The clone also retains its canonical-instance link. A fresh clone is created for each grant. Orb selection,
perk chance, quantity, owner and RNG stream usage are unchanged. This fixes both Preheated and the bonus-orb
reaction that shares the helper. Specific-type start-orb perks already use native `OrbCmd.Channel<T>`.
No Max Health mechanics or saves were changed.

## Verification

- Release build against installed v0.111.0: passed.
- `verification/OrbChannelCheck` runs the actual compiled helper against the real game model factory and RNG
  under .NET 9. It intercepts only the engine/animation boundary, applying the same `AssertMutable` requirement.
- Old installed DLL: reproduced `CanonicalModelException` (Plasma Orb, demonstrating the bug is not Glass-only).
- Corrected DLL: 200 grants across all five native orb types passed. All were separate mutable instances;
  canonical templates stayed canonical, owners matched, and the seeded orb sequence and RNG consumption matched.
- Full in-game combat startup and co-op still require gameplay verification. The regression check does not run
  combat animations or deal cards. No computer vision was used.

Run the check with:

```
dotnet run --project mod/verification/OrbChannelCheck/OrbChannelCheck.csproj -c Release -- <path-to-PerkTree.dll>
```
