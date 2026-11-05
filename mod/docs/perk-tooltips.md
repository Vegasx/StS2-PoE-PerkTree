# Perk hover tooltips

The tree header always shows points, committed/pending counts, allocation status, Refund pending, and Close.
Hovering a perk creates a native `NHoverTipSet` beside the pointer, independent of tree pan/zoom. It contains
the perk name, chance, allocation state, description, shared-enemy scope, and any allocation error.
The game handles native panel styling, fonts, companion stacking, and screen-edge overflow.
Perk tiers are never displayed. Numeric values in the perk description (including proc chances, magnitudes,
fixed costs, and thresholds) use native `[color=#rrggbb]` markup with the node's full editor color.
Companion descriptions retain their native formatting. Taken status appears below the description;
pending status and pending-allocation explanation lines are omitted from tooltips.
When a description has no `{c}` placeholder and the node's chance is below 100%, its generated chance
is an inline prefix: `25% Chance: Channel 1 random Orb(s) at the start of combat.` Descriptions that
already include `{c}` keep their authored wording. The prefix's percentage uses the same node color.

## Modified probability previews

The displayed chance is now the probability that the hovered node procs at least once on its trigger,
after the current allocation's capped Priming (`proc_boost`), multiplier (`proc_double`), and retry
(`proc_reroll`) totals. Pending allocations are included, matching the editor's allocation preview,
without adding a pending-allocation explanation line.

`ProcChancePreview.Calculate` is read-only and reuses `ModifierMath.ProcChance`, `ModifierMath.ProcRolls`,
`ChanceMath.Split`, and `ChanceMath.ToBasisPoints`. The last step matches the actual RNG's 0.01% rounding.
The combined probability is `1 - (1 - perRollChance)^rolls`, calculated without consuming RNG or executing hooks.
For example, two 7% attempts display `13.51% (7% × 2 rolls)`, both in authored `{c}%` descriptions and the
generated inline prefix. Retry counts are maximum attempts: gameplay stops once an attempt succeeds.

The tooltip also includes Frenzied's extra retry on `VigorSpentProc` handlers. Guaranteed/passive perks
are not increased by chance boosts. For chances over 100%, the primary chance is 100% and an additional
line shows guaranteed procs plus the combined odds of an extra proc, preserving the runtime's semantics.
Odds are per node per trigger; they do not merge separate nodes' independent effects or future turns.
Native keyword companion descriptions remain unchanged.

This changes display only: runtime rolling, chance modifiers, quantities, RNG consumption and saves are unchanged.
Release build and 139 Core tests passed, including seven new probability-preview tests and an exhaustive
two-attempt comparison against `ChanceMath.RollTotal`. Visual presentation still needs in-game confirmation.

`PerkHoverTips.cs` resolves the effect catalog's `keywords` through `HoverTipFactory.FromKeyword`,
`HoverTipFactory.Static`, or `HoverTipFactory.FromPower` with the canonical power from `ModelDb.AllPowers`.
For example, `Vulnerable` uses the game's localized Vulnerable power description. Unresolvable keywords
are logged once and skipped, so other companions still appear. Editor stand-in descriptions are not used.

The perk title is inserted under a `PERKTREE.<effect-id>.title` key in `static_hover_tips`, the localization
table used by the game's own static tooltip factory. The game does not have a table named `ui`.
Each hover owns a temporary cursor anchor; leaving the perk, closing/hiding the tree, moving the tree,
or leaving the map removes its tooltip. The SceneTree frame callback is disconnected when the layer exits.

`text.json` now includes the editor's `base` value for resistance descriptions. The display formatter
resolves `{x}` and uses `1 + node.Magnitude` for `{m}`, consistent with the proc multiplier calculation.

Validation on 2026-11-04:

- Release build against installed v0.111.0: passed (existing nullability warnings; offline NuGet audit warning).
- Existing Core suite: 132 passed.
- All 24 distinct keyword IDs currently exported resolve to native power, card keyword, or static tooltip sources
  in the installed assembly.
- First in-game attempt exposed the incorrect `ui` table name; corrected to the verified `static_hover_tips`
  table and rebuilt/reinstalled. The user confirmed both header behavior and corrected tooltip rendering.
- Follow-up removal of tier labels and addition of node-colored numeric values: release build passed;
  visual check deferred to the user (no computer vision).
- Editor validator has an existing error: Regent places the on/off perk `proc_reroll` twice. No tree or balance
  data was changed for this UI work.

The installed update consists of `PerkTree.dll` and `data/text.json` only. Backups of the previous installed
files are in the ignored `mod/backups/before-cursor-tooltips-*` directory. Restart the game to load the DLL.
