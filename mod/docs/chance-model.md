# Chance model

Every perk can succeed only some of the time. This is how the mod, the data files and the editor agree on it. Written 2026-11-01 (schema version 2).

## The model

A node gives an **amount** when it **succeeds**. Two numbers, kept apart:

| | where | meaning |
|---|---|---|
| amount | node `magnitude` | what one success gives (2 Block, 1 Strength, 0.25 = 25 points of a percent perk) |
| chance | effect `chance` (default for every node of the perk), node `chance` (optional override) | above 0, written as a fraction: 1 = always (the default, so it is left out of the files), 0.5 = half the time, 1.5 = one sure success plus a 50% roll. Highest allowed is 100 |

- Each allocated node rolls **on its own** each time the effect triggers, in node id order, with the game's seeded RNG. Three 50% nodes are three rolls.
- A chance past 1 gives its whole part as sure successes and rolls the rest: 1.5 = one sure success and a 50% roll. Limit: 100 successes per trigger.
- A node at exactly 1 never touches the RNG. A tree where nobody sets a chance behaves like plain sums.
- The *cap* of an effect (on/off switches, resists) caps the rolled amount too.
- **Luck perks** (`proc_boost`, `proc_double`, `proc_reroll`) only change nodes whose chance is below 1: `(chance + boost) x (1 + double)`, whole part guaranteed. Empowering (`proc_reroll`) is additive: each node grants one extra attempt after a failed roll. One node allows 2 total attempts; two allow 3. Stop at the first success. The limit is 5 total attempts, including Frenzied's extra attempt; Empowering's data cap is 4 retries.

## What the editor and exporter write

`mod/data/runtime/effects.json`: per effect, `chance` (a fraction, only written if below 1).
`mod/data/runtime/<character>.tree.json`: per node, `magnitude` (the amount, same scaling as before) and, only if it differs from the perk's, `chance`.
Both files have `"schemaVersion": 2`. Version 1 stored the chance in `magnitude` for a few perks; the mod refuses version 1 files.

`perks.json` needs no new fields: its `chance` (percent) is the perk default and `unit` is the amount per success. A tree node may carry `chance` (percent) as an override; `tools/export-mod.mjs` passes it through.

## What the mod can roll

`node tools/scan-mod.mjs` writes `data/mod-support.json`. Per effect, `rollsChance`:

- `true`: the handler rolls the chance when it triggers (87 perks today).
- `false`: the mod ignores a chance below 100% and the exporter reports an error. These 16 stay guaranteed for now: `vulnerable_resist`, `weak_resist`, `frail_resist`, `proc_boost`, `proc_double`, `proc_reroll`, `potion_boost`, `potion_double`, `start_power_boost`, `low_hp_draw`, `execute_bonus`, `loss_invert`, `ethereal_save`, `spread_debuffs_on_kill`, `doom_defer`, `gold_armor`. The first group are numbers the game asks about many times (hand size, damage, resists), so they cannot roll mid-event; the rest are "do I own it" switches or modifiers of other perks.
- perks with no code yet are accepted with any chance and do nothing in game.

The list lives in `HandlerRegistry.GuaranteedOnly` in the mod.

## When the roll happens, per kind of perk

The roll is at the moment the perk would act, not when the combat starts:

- Event perks (on kill, on card played, on potion used...): one roll per event.
- Combat-start and turn-start perks: one roll at that moment.
- Once-per-combat / once-per-turn triggers (decided 2026-11-02): the trigger is only used up when the effect actually happens. A failed roll leaves it available, so the next chance to trigger tries again, until one succeeds. Applies to Renewing, Insightful, Studious, Overflowing, Cursefed, Bloodforged, Focused, Savage, and to the 3-a-turn limit of Resonant and Surging (only a bonus that happens counts).
- "Every Nth" perks (every 4th turn, every 12th attack): the count goes on regardless, the roll happens when the Nth comes up.
- "Per X" perks (per relic, per 300 gold, per 2 HP lost, per Energy gained): one roll, then multiplied by X.
- "Each Vigor point spent" and "each unused Vigor": one roll per point, per perk.
- Package perks (`curse_count_ramp`): one roll for the whole package.
- Perks that mean a count (`retain_random`, `random_debuff`): the amount is how many.
- Cost keystones (Immolating, Overclocked, Echoing, Eroding): the benefit rolls, the cost does not. The HP or Max HP is always paid; Echoing's debuffs always come when you play the marked card.
- `focus_when_full` rolls each time the Orb slots become full and takes the Focus back when they stop being full.
- "Apply more" perks: the amount is how many extra stacks.

## Kept in step by hand

- The luck formula (here, in `ChanceMath.RollTotal`, and in the editor's "With your luck perks" tooltip line).
- `HandlerRegistry.GuaranteedOnly` (scan-mod.mjs reads it; nothing else needs updating).
