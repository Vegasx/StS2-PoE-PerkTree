# StS2 PoE Perk Tree

A perk tree for a Slay the Spire 2 mod: players spend points earned during a run on tree nodes, each node grants one perk effect for the rest of that run. This repo holds the **data** (perk list and one tree per character), an **editor** to change it, and a **validator**. The mod reads the same data files.

## Layout

```
data/
  perks.json       every perk effect: name, scope, kind, per-node number, cap, text
  keywords.json    stand-in text for companion tooltips (Thorns, Vulnerable, ...)
  pairs.json       which perks pair well and which work against each other
  modules.json     module definitions and slot order, used only to generate a default layout
  trees/<character>.json   one tree layout per character
editor/index.html  the editor (a single file, no build step)
tools/validate.mjs   command-line checks (Node 18 or newer, no dependencies)
tools/scan-mod.mjs   reads the mod's C# handlers and writes data/mod-support.json
tools/export-mod.mjs writes the files the mod loads, from data/ (see "Using this data in the mod")
data/mod-support.json which perks the mod can run today, generated
docs/mod-integration.md  how the editor's data maps to the mod, and what the mod still needs
mod/                 the mod's code (its own README inside)
```

## Using the editor

1. Open `editor/index.html` in **Chrome or Edge**.
2. Click **Open repo folder** and choose the folder that contains `data/`. The browser asks once per session for permission to edit it.
3. Change things. Every edit is written straight back to the files in `data/` (the top bar shows "Saved to disk"). Then commit as usual: `git diff` shows exactly what changed.

Three tabs:

- **Play**: allocate perks, win fights for points, pick the next map node to lock. For feeling out a build.
- **Edit**: tools to select and move nodes (V), add nodes (A), connect nodes (C) and delete (X). Click a node to open the side panel: type, perk, colour, position and connections. Ctrl+Z undoes. **Import / export** shows a tree as JSON, **Reset layout** regenerates the default from `modules.json` (undoable).
- **Balance**: edit every perk's name, scope, kind, per-node number, trigger chance (empty means guaranteed), cap, text and note. Changed rows are marked, with a Revert button.

Without a folder (for example served over http for a quick look) the editor is read only. Serving the repo with `python -m http.server` and opening `http://localhost:8000/editor/` works for that.

## Checking the data

```
node tools/validate.mjs
```

Errors (exit code 1): bad JSON, unknown perk ids, edges to missing nodes, a tree with no start node, perks that cannot be reached, on/off perks placed twice, a chance on a perk the mod never rolls. Perks you left out of a tree are only listed as a note, never an error. So is a perk placed more times than its cap allows (the extra is simply wasted). Warnings: another character's perk placed in a tree (allowed when you mean it, but it does nothing if that character cannot trigger it), keywords with no tooltip source yet, keystones far from their target distance in `modules.json` (`targets`).

## File formats

### `data/perks.json`

```json
{"id":"start_block","name":"Anchored","group":"general","scope":"S","kind":"add","unit":3,"unit_label":"Block","text":"Start each combat with {v} Block."}
```

| field | meaning |
|---|---|
| `id` | stable id, used by trees and by the mod |
| `group` | `general`, a character id (`ironclad`, `silent`, ...) or `vigor` |
| `scope` | `S` self, `E` enemies through you, `X` shared enemy modifier (changes the enemy for every player) |
| `kind` | `add` sums per node, `flag` is on/off and is placed once (`pct` is a legacy kind, no perk uses it any more) |
| `unit` | the amount one node gives (1 Energy, 3 Block, 25 for +25% damage, ...) |
| `cap` | optional ceiling for the total |
| `chance` | optional trigger chance in percent, above 0 and at most 10000. Missing means 100 (guaranteed); above 100 gives sure successes plus a roll for the rest (150 = one sure success and a 50% roll). Every placed perk of the effect rolls on its own, and a tree node can override it with its own `chance`. This is where "25% chance to gain 1 Energy" lives: `unit` 1, `chance` 25 |
| `base` | used by `{x}` in resist perks (the vanilla size of the debuff) |
| `multiplier` | true for a perk whose total shows as `x(1+nodes)` |
| `tier` | `key` marks a keystone with a built-in cost |
| `keywords` | the game's own keyword ids this perk mentions, as in the mod's effect catalog |
| `percent` | true when `unit` is in percent points and the mod stores a fraction (25 becomes 0.25) |
| `text` | player text. Tokens: `{v}` total of all placed perks, `{u}` the amount one perk gives, `{m}` 1 + node count, `{x}` base minus total, `{d}` double the total, `{h}` half the total, `{c}` the trigger chance. Text without `{c}` shows "N% chance:" in front when the chance is below 100. With a chance below 100 the text describes one perk, so use `{u}` rather than `{v}` |

### `data/trees/<character>.json`

```json
{"version":1,
 "nodes":[{"id":"n0","x":0,"y":0,"type":"start","fx":"start_strength","color":"#e3b341","mod":"start","tag":null}, ...],
 "edges":[{"a":"n0","b":"n9","arc":null,"slot":null}, ...]}
```

- `type`: `minor`, `notable`, `cap` (capstone), `socket` or `start`. A socket is a free joint: it costs nothing and its neighbours become adjacent.
- `fx`: perk id (ignored for sockets). `color`: display colour. `mod` and `tag`: which module a node came from, used for highlighting.
- `arc`: `null` for a straight line, otherwise `{rx,ry,rot,sweep}` as in an SVG elliptical arc.
- Reaching a perk costs one point per perk node on the shortest path from the nearest start node, sockets free. Allocation needs an adjacent allocated node, except for a `start` node: a tree can have any number of them, and each one can be taken at any time without a neighbour (it still costs a point).

## Using this data in the mod

The mod (in `mod/`) reads a different shape: one effect catalog plus one `<character>.tree.json` per character, with a magnitude on every node and no sockets. Do not point it at `data/` directly. Run the exporter instead:

```
node tools/scan-mod.mjs      # after the mod's handlers change: refreshes data/mod-support.json
node tools/export-mod.mjs    # writes mod/data/runtime/effects.json and <character>.tree.json
```

Copy those files into the `data` folder next to `PerkTree.dll` (`<game>\mods\PerkTree\data`) to try them without rebuilding. The exporter refuses to write anything while there are errors. `docs/mod-integration.md` has the field-by-field mapping, what the mod still needs, and the one-line project change that ships the files with the build.

What the exporter and the checks know about the mod, read from its source by `scan-mod.mjs`:
- which perks have code at all (all 112 today), and which of those the mod rolls a chance for (94). The other 18 are always 100%: the resists, the luck perks, `execute_bonus`, `low_hp_draw`, `potion_slot`, `max_hp` and a few switches. The editor locks their chance field and the exporter reports an error if one is set below 100;
- a perk's `chance` is the default for every node of it, and a single node can override it (Edit mode, side panel). Each node rolls on its own. The full rules are in `mod/docs/chance-model.md`.
- a perk's number (`unit`) is the default amount for every node of it, and a single node can override that too (Edit mode, side panel, "Amount"). The override is saved on the node as `amount` and the exporter writes it as that node's magnitude, so totals, rolls and tooltips use the real amounts. On/off perks have no amount.

## Design notes

The design, audit and recon notes live in the Relic Forge project on claude.ai. Working perk names are the old Relic Forge prefix names.
