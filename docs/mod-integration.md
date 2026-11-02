# Editor data and the mod

Written 2026-11-01. The editor in this repo and the mod in `mod/` were built separately; this is how they meet.

## The flow

```
editor  ->  data/perks.json + data/trees/*.json
        ->  node tools/export-mod.mjs
        ->  mod/data/runtime/effects.json + <character>.tree.json     (what PerkData.cs loads)
```

`node tools/scan-mod.mjs` goes the other way: it reads `mod/src/PerkTree.Mod/Effects/*.cs` and writes `data/mod-support.json`, which the editor, `validate.mjs` and the exporter use to say what the mod really does with each perk.

## Field mapping

| editor | mod | note |
|---|---|---|
| perk `id` | `EffectDef.Id` | same 103 ids, scopes match the mod's catalog |
| `scope` S / E / X | `self` / `enemyThroughMe` / `sharedEnemy` | |
| `group` ironclad, silent, ... | `characters: ["IRONCLAD"]` | `general` and `vigor` have none, as in the mod. It says who the perk is meant for: using it in another character's tree is allowed and only warned about (the mod loads it) |
| `keywords` | `keywords` | seeded from `effects.draft.json`, native ids |
| `kind: flag` | `cap: 1.0` | |
| `cap` | `cap` | divided by 100 when `percent` is true |
| `unit` | node `magnitude` (the amount one success gives) | divided by 100 when `percent` is true (Executing, Priming, the three resists) |
| `chance` (perk default, percent) | effect `chance` | 25 becomes 0.25, left out at 100. A tree node may carry its own `chance` (percent), written to the node only when it differs |
| node `amount` (layout only) | tree node `magnitude` | A node may carry its own `amount`, in the perk's units; the exporter scales it like the perk's number (a percent perk divides by 100) and writes it as that node's magnitude instead of the perk's. The mod needs nothing new: it already sums per-node magnitudes and rolls each node with its own |
| node type `minor` / `notable` / `cap` | `small` / `notable` / `keystone` | `start` becomes `small` and goes in `starts` |
| node `tag` | node `branch` | travel ring and spoke nodes are branch `travel` |
| sockets | (none) | the mod has no sockets: the exporter links each socket's neighbours directly |
| `x`, `y` | `x`, `y` | |

Node ids are kept exactly, so run saves (`current_run.save.perktree.json`) keep working while a tree is edited. They only break if you use Reset layout or import a layout with other ids; the mod already drops and refunds missing nodes.

## What the mod still needs

The chance model is settled and written up in `mod/docs/chance-model.md` (schema version 2): a node gives its amount when it succeeds, the chance is separate, per perk with an optional per-node override. The editor, exporter and validator follow it. What is left:

1. **16 perks are always 100%** (a chance of any other value is ignored, and the loader logs it) (`HandlerRegistry.GuaranteedOnly`): the resists, luck perks, potion and start-power boosts, `low_hp_draw`, `execute_bonus`, `loss_invert`, `ethereal_save`, `spread_debuffs_on_kill`, `doom_defer`, `gold_armor`. The editor locks their chance field.
2. **Every perk has code now.** None of the newest handlers (the cost keystones and the Shiv, Orb, Osty, Stars and Forge perks) has been run in the game yet; `mod/docs/open-decisions.md` lists what to watch for.
3. **Shipping the files.** Done: `PerkTree.Mod.csproj` copies `mod/data/runtime/*.json` into the build's `data/` folder (the samples in `mod/data/sample` are only for the unit tests). Re-run `node tools/export-mod.mjs` after every data change, then rebuild.
4. **Resist caps.** The exporter writes caps of 0.5 (Vulnerable) and 0.25 (Weak, Frail), the vanilla size of each debuff. The mod enforces the same limit at runtime.
5. **Doom.** Undying is 14 points from the start in the generated layout, the audit asked for about 30. The exporter and `validate.mjs` print the distance.

## Kept in step by hand

- `ModifierMath` and `ChanceMath.RollTotal` (proc_boost, proc_double, proc_reroll): the editor's tooltip line "With your luck perks" repeats their formula, `(chance + boost) x (1 + double)` for rolls below 100%, whole part guaranteed, up to 5 tries.
- Keyword text in `keywords.json` is stand-in text.
