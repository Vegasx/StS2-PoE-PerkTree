#!/usr/bin/env python3
"""
Prefix audit (design doc build step 1).

Reads the old Relic Forge PrefixTable.cs, maps EVERY prefix to a disposition, and writes:
  data/audit/audit.json          full record per prefix + per effect
  data/audit/effects.draft.json  effect catalog in the format `perktree validate` reads
  docs/prefix-audit.md           human-readable summary

It refuses to finish if any prefix in the old table has no disposition, so the audit cannot silently skip one.

usage: build_audit.py <path to old PrefixTable.cs> <repo root>
"""
import json, re, sys
from collections import OrderedDict, defaultdict

# --------------------------------------------------------------------------------------------------------------
# Scope rule (design doc section 3). When an effect touches several, the WIDEST wins, because the tooltip label
# exists to warn about the widest reach:  Self < EnemyThroughMe < SharedEnemy.
#   self            only the owner's own state (powers on self, block, draw, energy, gold, HP, own cards)
#   enemyThroughMe  only changes what the owner deals to enemies (bonus damage, damage pings)
#   sharedEnemy     leaves a state on enemies that every player's attacks interact with
#                   (Vulnerable/Weak/Poison/Doom on enemies, stripping or spreading enemy powers)
# --------------------------------------------------------------------------------------------------------------

def E(scope, unit, hook, kw=(), cap=None, chars=(), status="port", note="", sources=(), tier=None, placement=None, branch=None, new=False):
    # tier: suggested node tier (None = author's choice). placement: where in the tree it should sit.
    # branch: PROPOSED general branch (None = unassigned); a proposal for the tree author, not enforced anywhere.
    return dict(scope=scope, unit=unit, hook=hook, keywords=list(kw), cap=cap, characters=list(chars),
                status=status, note=note, sources=list(sources), tier=tier, placement=placement, branch=branch, new=new)

SELF, ETM, SHARED = "self", "enemyThroughMe", "sharedEnemy"
CMB = "AfterRoomEntered (room is CombatRoom) + PowerCmd.Apply"      # the game's documented start-of-combat pattern
TURN = "AfterPlayerTurnStart"
PLAYED = "AfterCardPlayed"

# effect id -> record. `sources` is filled in from NAMES below.
EFFECTS = OrderedDict([
    # ---- combat-start stat grants ----
    ("start_thorns",      E(SELF, "+1 Thorns per node", CMB, ["Thorns"])),
    ("start_block",       E(SELF, "+3 Block per node", CMB, ["Block"])),
    ("start_strength",    E(SELF, "+1 Strength per node", CMB, ["Strength"])),
    ("start_dexterity",   E(SELF, "+1 Dexterity per node", CMB, ["Dexterity"])),
    ("start_vigor",       E(SELF, "+2 Vigor per node", CMB, ["Vigor"], note="Roused gave 3, Ferocious 2; unit takes the lower, tune by node count.", branch="vigor")),
    ("start_artifact",    E(SELF, "+1 Artifact per node", CMB, ["Artifact"])),
    ("start_buffer",      E(SELF, "+1 Buffer per node", CMB, ["Buffer"])),
    ("start_blur",        E(SELF, "+1 Blur per node", CMB, ["Blur"])),
    ("start_regen",       E(SELF, "+2 Regen per node", CMB, ["Regen"])),
    ("start_heal",        E(SELF, "+1 HP healed on turn 1 per node", "AfterPlayerTurnStart (round 1) + heal command")),
    ("start_vulnerable_enemies", E(SHARED, "+1 Vulnerable to all enemies per node", CMB, ["Vulnerable"],
                                   note="Old Intimidating applied it on turn 2 only to weaken a min-1 effect; with per-node magnitudes the delay is unnecessary.")),
    ("gold_strength",     E(SELF, "+1 Strength per 300 gold, per node", CMB, ["Strength"])),
    # ---- per-turn / per-combat triggers ----
    ("second_wind_block", E(SELF, "+8 Block, first time HP <= 50% each combat, per node", "AfterCurrentHpChanged / AfterDamageReceived", ["Block"])),
    ("enduring_strength", E(SELF, "+1 Strength each turn from turn 5, per node", TURN, ["Strength"])),
    ("first_turn_draw",   E(SELF, "+1 card drawn on turn 1, per node", "ModifyHandDraw (round 1)")),
    ("low_hp_draw",       E(SELF, "+1 card drawn per turn while HP <= 50%, per node", "ModifyHandDraw")),
    ("turn_vigor",        E(SELF, "+1 Vigor each turn per node", TURN, ["Vigor"], branch="vigor")),
    ("turn_ping_damage",  E(ETM, "+1 damage to all enemies each turn per node", TURN + " + damage command")),
    ("energy_every_n_turns", E(SELF, "+1 Energy every 4th turn, per node", TURN, ["Energy"])),
    ("energy_per_attacks",   E(SELF, "+1 Energy per 12 attacks played, per node", PLAYED, ["Energy"])),
    ("draw_on_first_hit", E(SELF, "+1 card drawn the first time you are hit each combat, per node", "AfterDamageReceived")),
    ("skill_damage_all",  E(ETM, "+1 damage to all enemies per 4 Skills in a turn, per node", PLAYED)),
    ("attack_strength",   E(SELF, "+1 Strength per 4 Attacks in a turn, per node", PLAYED, ["Strength"])),
    ("attack_block",      E(SELF, "+1 Block per 4 Attacks in a turn, per node", PLAYED, ["Block"])),
    ("attack_dexterity",  E(SELF, "+1 Dexterity per 4 Attacks in a turn, per node", PLAYED, ["Dexterity"])),
    ("end_turn_no_block", E(SELF, "+1 Block if you end your turn with none, per node", "BeforeSideTurnEnd", ["Block"])),
    ("retain_random",     E(SELF, "Retain +1 random card at end of turn, per node", "BeforeFlush", ["Retain"])),
    ("power_first_draw",  E(SELF, "+2 cards drawn on the first Power played each combat, per node", PLAYED)),
    ("power_energy",      E(SELF, "+1 Energy when you play a Power (once/turn), per node", PLAYED, ["Energy"])),
    ("skill_draw_chance", E(SELF, "+25% chance to draw 1 when you play a Skill, per node", PLAYED)),
    ("exhaust_dodge",     E(SELF, "+25% chance an exhausting card is discarded instead, per node", "Harmony patch on CardCmd.Exhaust (no model hook)", ["Exhaust"])),
    ("ethereal_save",     E(SELF, "an Ethereal card that would exhaust is discarded instead (on/off)", "Harmony patch on CardCmd.Exhaust (no model hook)", ["Ethereal", "Exhaust"], cap=1.0)),
    ("kill_gold",         E(SELF, "+3 gold per enemy killed, per node", "AfterDeath")),
    ("kill_block",        E(SELF, "+3 Block per enemy killed, per node", "AfterDeath", ["Block"])),
    ("execute_bonus",     E(ETM, "+25% damage to enemies at <= 20% HP, per node", "ModifyDamageMultiplicative")),
    ("hit_energy_chance", E(SELF, "+25% chance for +1 Energy on unblocked enemy damage, per node", "AfterDamageReceived", ["Energy"])),
    ("spread_debuffs_on_kill", E(SHARED, "on killing a Vulnerable enemy, its debuffs spread to another (on/off)", "AfterDeath",
                                 ["Vulnerable", "Weak", "Frail", "Poison"], cap=1.0)),
    # ---- reactive ----
    ("gain_amplify",      E(SELF, "gain +1 more when you gain Strength/Dexterity (max 3/turn)", "AfterPowerAmountChanged", ["Strength", "Dexterity"],
                            note="Its old cost (a bundled enemy-rider curse) no longer exists; whether it needs a new one is a balance call, left to you.", branch="strength_defense")),
    ("loss_invert",       E(SELF, "enemy Strength/Dexterity reduction becomes a gain (on/off)", "TryModifyPowerAmountReceived", ["Strength", "Dexterity"], cap=1.0, branch="strength_defense", note="Decided: strength and defense branch, with gain_amplify (both react to Strength/Dexterity changes).")),
    ("energy_discharge",  E(ETM, "+4 damage to all enemies per bonus Energy gained, per node", "Harmony patch on PlayerCmd.GainEnergy (AfterModifyingEnergyGain has no arguments)", ["Energy"], branch="gamble", note="Decided: Gamble branch, as the payoff for the energy keystones. energy_for_hp and energy_for_max_hp grant bonus Energy every turn, which triggers this each turn; it also works with any other bonus Energy, so it is never dead weight.")),
    # ---- gambles: effects with a built-in cost (keystone candidates) ----
    ("energy_for_hp", E(SELF, "+1 Energy per turn, lose 5 HP at combat start", TURN + " + combat start", ["Energy"], note="Keystone (built-in cost).", tier="keystone", branch="gamble")),
    ("energy_for_max_hp", E(SELF, "+1 Energy per turn, lose 1 Max HP permanently each combat start", TURN + " + combat start", ["Energy"],
                            note="Keystone. The permanent Max HP loss recurs every combat in a run; check the totals before keeping it.", tier="keystone", branch="gamble")),
    ("replay_grant", E(SELF, "each turn a random hand card gains Replay 1; playing it costs Vulnerable 1 + Frail 1", TURN + " + " + PLAYED, ["ReplayStatic", "Vulnerable", "Frail"],
                            note="Keystone (built-in cost).", tier="keystone", branch="gamble")),
    ("enemy_strip",       E(SHARED, "each turn move one enemy power 1 toward zero", TURN, note="Keystone (decided). Strips enemy buffs AND teammates' debuffs on enemies; a gamble by design.", tier="keystone", branch="gamble")),
    ("symmetric_vulnerable", E(SHARED, "Vulnerable 1 to one enemy and to yourself at combat start, per node", CMB, ["Vulnerable"],
                              note="Decided: owner-only. The old code picked a random player from the whole party; now it is you (identical in solo).", branch="gamble")),
    ("symmetric_weak",    E(SHARED, "Weak 1 to one enemy and to yourself at combat start, per node", CMB, ["Weak"],
                            note="Decided: owner-only (see symmetric_vulnerable).", branch="gamble")),
    ("random_debuff",     E(SHARED, "each turn 50%: Vulnerable/Weak/Frail 1 to a random enemy or to yourself, per node", TURN, ["Vulnerable", "Weak", "Frail"],
                            note="Decided: owner-only. The old code could pick any player in the party.", branch="gamble")),
    ("gold_armor", E(SELF, "enemy hits deal 1 less, costing 5 gold each time", "ModifyDamageAdditive (hits on the owner)",
                            note="Keystone (built-in cost).", tier="keystone", branch="gamble")),
    # ---- meta / auras (act on OTHER perks) ----
    ("proc_double",       E(SELF, "other perks' random procs happen twice as often", "PerkProc.Chance helper (not a game hook)",
                            note="Old text says relic prefixes and curses; reword to perks. Needs the PerkProc plumbing. Stacking: multiplier = 1 + total.", branch="gamble")),
    ("proc_boost",        E(SELF, "+10 points to other perks' random proc chances", "PerkProc.Chance helper", note="Reword relics -> perks. Needs the PerkProc plumbing.", branch="gamble")),
    ("proc_reroll",       E(SELF, "other perks' random procs get a second roll", "PerkProc.Chance helper", note="Reword relics -> perks. Needs the PerkProc plumbing.", branch="gamble")),
    ("start_power_boost", E(SELF, "+1 to combat-start defensive perks (Artifact/Buffer/Blur)", CMB, note="Reword relics -> perks; depends on start_artifact/buffer/blur.")),
    ("potion_double",     E(SELF, "your potion-use perk effects are doubled", "PerkProc helper", note="Reword; depends on the potion_* effects.")),
    ("potion_boost",      E(SELF, "+1 to each potion-use perk amount", "PerkProc helper", note="Reword; depends on the potion_* effects.")),
    # ---- potion family ----
    ("potion_block",      E(SELF, "+4 Block on potion use, per node", "BeforePotionUsed", ["Block"])),
    ("potion_strength",   E(SELF, "+1 Strength on potion use, per node", "BeforePotionUsed", ["Strength"])),
    ("potion_dexterity",  E(SELF, "+1 Dexterity on potion use, per node", "BeforePotionUsed", ["Dexterity"])),
    ("potion_vulnerable", E(SHARED, "+1 Vulnerable to all enemies on potion use, per node", "BeforePotionUsed", ["Vulnerable"])),
    ("potion_weak",       E(SHARED, "+1 Weak to all enemies on potion use, per node", "BeforePotionUsed", ["Weak"])),
    ("potion_buffer",     E(SELF, "+25% chance of 1 Buffer on potion use, per node", "BeforePotionUsed", ["Buffer"])),
    # ---- run-state ----
    ("curse_draw_stat",   E(SELF, "drawing a Curse card gives +1 Strength or Dexterity (once/turn), per node", "AfterCardDrawn", ["Strength", "Dexterity"])),
    ("block_per_relic",   E(SELF, "+2 Block per relic carried at combat start (max 8), per node", CMB, ["Block"],
                            note="Old Attuned counted OTHER FORGED relics; now counts relics carried.")),
    ("curse_count_ramp",  E(SELF, "combat-start ramp by number of Curse cards in your deck (1: +1 Str; 2: +1 Str +1 Dex; 3: +2/+2; 4: +3/+2; 5+: +3/+3 and Intangible 1), once per node",
                            CMB + "; count Player.Deck cards with Type == Curse", ["Strength", "Dexterity", "Intangible"],
                            note="Decided: count Curse cards in the deck (the old code counted Relic Forge curses on relics). Combat-created curses do not count; the deck is the run deck.")),
    # ---- NEW DESIGN (not ports): resistance to your own debuffs, for the Gamble branch ----
    ("vulnerable_resist", E(SELF, "Vulnerable is 0.10 (10 points) less effective on you, per node: +50% damage taken becomes +40%",
                            "Harmony postfix on VulnerablePower.ModifyDamageMultiplicative (game hardcodes its modifiers; no model hook)", ["Vulnerable"],
                            branch="gamble", new=True,
                            note="Applied last, to the game's final multiplier, so Debilitate etc. still count (a stacked-resistance player is not flatly immune to them). Floors at neutral. Natural cap = the vanilla size; the UI must read it from the power. Place past the cost nodes so the player pays first.")),
    ("weak_resist",       E(SELF, "Weak is 0.10 (10 points) less effective on you, per node: -25% damage dealt becomes -15%",
                            "Harmony postfix on WeakPower.ModifyDamageMultiplicative (game hardcodes its modifiers; no model hook)", ["Weak"],
                            branch="gamble", new=True,
                            note="Same rules as vulnerable_resist. Base value 0.75 (read from the game's IL). Weak is a smaller effect than Vulnerable (1.5), so the same 10 points is a larger share of it; per-node size is yours to tune.")),
    ("frail_resist",      E(SELF, "Frail is 0.10 (10 points) less effective on you, per node: -25% block gained becomes -15%",
                            "Harmony postfix on FrailPower.ModifyBlockMultiplicative (game hardcodes its modifiers; no model hook)", ["Frail"],
                            branch="gamble", new=True,
                            note="Same rules as vulnerable_resist. Base value 0.75 (read from the game's IL).")),
    ("vigor_strength_pct",  E(SELF, "each Vigor consumed: +15% chance of 1 Strength, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Strength"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_block_pct",     E(SELF, "each Vigor consumed: +25% chance of 2 Block, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Block"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_vulnerable_pct",E(SHARED, "each Vigor consumed: +20% chance of Vulnerable 1 to a random enemy, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Vulnerable"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_weak_pct",      E(SHARED, "each Vigor consumed: +20% chance of Weak 1 to a random enemy, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Weak"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_energy_pct",    E(SELF, "each Vigor consumed: +10% chance of 1 Energy, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Energy"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_regen_pct",     E(SELF, "each Vigor consumed: +20% chance of 1 Regen, per node", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor", "Regen"], note="Does not fire on the turn-end conversion (Congealing/Evolving), as in the old mod.", branch="vigor")),
    ("vigor_gain_amplify",  E(SELF, "gaining Vigor gives +1 more (max 3 times/turn) (on/off)", "AfterPowerAmountChanged", ["Vigor"], cap=1.0, branch="vigor")),
    ("vigor_unused_to_block", E(SELF, "end of turn: unused Vigor converts to Block 1:1 (on/off)", "BeforeSideTurnEnd", ["Vigor", "Block"], cap=1.0, branch="vigor")),
    ("vigor_unused_strength_pct", E(SELF, "end of turn: consume unused Vigor, +18% chance of 1 Strength each, per node", "BeforeSideTurnEnd", ["Vigor", "Strength"], branch="vigor")),
    ("vigor_proc_double", E(SELF, "other Vigor-consume perks proc twice as often; 5% self-debuff and 1% stat loss on consume", "AfterPowerAmountChanged (power is VigorPower, amount < 0)", ["Vigor"],
                              note="Keystone (built-in cost: 5% self-debuff and 1% stat loss per consume). Reword relics -> perks. Not fired by the turn-end conversion, as in the old mod.", tier="keystone", branch="vigor")),
    # ---- character: IRONCLAD ----
    ("exhaust_block",       E(SELF, "+2 Block whenever a card is exhausted, per node", "AfterCardExhausted", ["Exhaust", "Block"], chars=["IRONCLAD"])),
    ("first_hp_loss_strength", E(SELF, "+2 Strength the first time you lose HP each combat, per node", "AfterDamageReceived", ["Strength"], chars=["IRONCLAD"])),
    ("vulnerable_extra_chance", E(SHARED, "+25% chance to apply 1 more Vulnerable, per node", "AfterPowerAmountChanged", ["Vulnerable"], chars=["IRONCLAD"])),
    ("hp_lost_vigor",       E(SELF, "turn start: gain Vigor equal to half the HP lost since last turn, per node", TURN, ["Vigor"], chars=["IRONCLAD"])),
    # ---- character: SILENT ----
    ("poison_extra_chance", E(SHARED, "+50% chance to apply 1 more Poison, per node", "AfterPowerAmountChanged", ["Poison"], chars=["SILENT"])),
    ("shiv_duplicate_chance", E(SELF, "+25% chance a played Shiv adds a Shiv to hand, per node", PLAYED, chars=["SILENT"],
                                note="Keyword gap: Shiv is a card, needs a card tooltip.")),
    ("discard_draw",        E(SELF, "draw a card whenever you discard one, per node", "AfterCardDiscarded", chars=["SILENT"])),
    ("shiv_return_chance",  E(SELF, "+25% chance an exhausted Shiv returns to hand, per node", "AfterCardExhausted", ["Exhaust"], chars=["SILENT"],
                              note="Keyword gap: Shiv.")),
    # ---- character: DEFECT ----
    ("first_focus_bonus",   E(SELF, "the first time you gain Focus each combat, gain 1 more, per node", "AfterPowerAmountChanged", ["Focus"], chars=["DEFECT"])),
    ("orb_channel_chance",  E(SELF, "+25% chance a Channeled orb Channels a random orb too, per node", "AfterOrbChanneled", ["Channeling"], chars=["DEFECT"],
                              note="Keyword gap: Orb needs an orb tooltip.")),
    ("focus_when_full",     E(SELF, "+1 Focus while orb slots are full, per node", "AfterOrbChanneled / evoke + slot count", ["Focus"], chars=["DEFECT"])),
    ("start_channel_orb",   E(SELF, "Channel a random orb at combat start, per node", CMB, ["Channeling"], chars=["DEFECT"], note="Keyword gap: Orb.")),
    # ---- character: NECROBINDER ----
    ("summon_block",        E(SELF, "+3 Block when you Summon, per node", "AfterSummon", ["Block", "SummonStatic"], chars=["NECROBINDER"])),
    ("doom_extra",          E(SHARED, "when you apply Doom, apply 1 more, per node", "AfterPowerAmountChanged", ["Doom"], chars=["NECROBINDER"])),
    ("summon_each_turn",    E(SELF, "Summon 1 each turn, per node", TURN, ["SummonStatic"], chars=["NECROBINDER"], note="Keyword gap: Osty.")),
    ("doom_defer", E(SELF, "Doom no longer kills you outright; the next HP you lose kills you instead (on/off)", "Harmony patch on DoomPower.DoomKill", ["Doom"],
                              cap=1.0, chars=["NECROBINDER"], note="Rewrites a core death rule. Keystone, placed about 30 points in. Needs a Harmony patch on DoomPower.DoomKill (an event-driven call, not per-frame).", tier="keystone", placement="about 30 allocated points from a start node (your call)")),
    ("turn_summon_from_hp_lost", E(SELF, "turn start: Summon 2x the HP lost since last turn, per node", TURN, ["SummonStatic"], chars=["NECROBINDER"])),
    ("turn_block_from_summon_hp", E(SELF, "turn start: Block equal to the HP your summon lost, per node", TURN, ["Block"], chars=["NECROBINDER"])),
    # ---- character: REGENT ----
    ("star_each_turn",      E(SELF, "+1 Star each turn, per node", TURN, chars=["REGENT"], note="Keyword gap: Stars.")),
    ("forge_star",          E(SELF, "+1 Star when you Forge, per node", "AfterForge", ["Forge"], chars=["REGENT"], note="Keyword gap: Stars.")),
    ("star_refund_chance",  E(SELF, "+50% chance to refund 1 Star when you spend Stars, per node", "AfterStarsSpent", chars=["REGENT"], note="Keyword gap: Stars.")),
    ("forge_draw_chance",   E(SELF, "when you Forge: +50% chance to draw 1 card, per node", "AfterForge", ["Forge"], chars=["REGENT"],
                              note="Decided: the old second chance (+25% to draw 2) is dropped. Several nodes of this one effect stack to get the same result if wanted later.")),
    ("star_gain_chance",    E(SELF, "+33% chance to gain 1 more Star when you gain Stars, per node", "AfterStarsGained", chars=["REGENT"], note="Keyword gap: Stars.")),
])

# old prefix name -> (effect id, optional note). Several names may map to one effect ("merge").
NAMES = {
    "Thorned": "start_thorns", "Barbed": "start_thorns",
    "Anchored": "start_block", "Bulwarked": "start_block",
    "Honed": "start_strength", "Mighty": "start_strength",
    "Nimble": "start_dexterity", "Supple": "start_dexterity",
    "Ferocious": "start_vigor", "Roused": "start_vigor",
    "Warding": "start_artifact", "Warded": "start_buffer", "Afterimage": "start_blur", "Regenerating": "start_regen",
    "Vital": "start_heal", "Intimidating": "start_vulnerable_enemies", "Gilded": "gold_strength",
    "Renewing": "second_wind_block", "Enduring": "enduring_strength", "Preemptive": "first_turn_draw", "Cornered": "low_hp_draw",
    "Invigorated": "turn_vigor", "Quicksilver": "turn_ping_damage", "Rhythmic": "energy_every_n_turns",
    "Accelerating": "energy_per_attacks", "Insightful": "draw_on_first_hit", "Bladed": "skill_damage_all",
    "Relentless": "attack_strength", "Gusting": "attack_block", "Darting": "attack_dexterity", "Tempered": "end_turn_no_block",
    "Retaining": "retain_random", "Studious": "power_first_draw", "Overflowing": "power_energy", "Cunning": "skill_draw_chance",
    "Tenacious": "exhaust_dodge", "Persistent": "ethereal_save", "Plundering": "kill_gold", "Finishing": "kill_block",
    "Executing": "execute_bonus", "Adrenal": "hit_energy_chance", "Contagious": "spread_debuffs_on_kill",
    "Resonant": "gain_amplify", "Obstinate": "loss_invert", "Discharging": "energy_discharge",
    "Immolating": "energy_for_hp", "Overclocked": "energy_for_max_hp", "Echoing": "replay_grant", "Eroding": "enemy_strip",
    "Exposing": "symmetric_vulnerable", "Enervating": "symmetric_weak", "Chaotic": "random_debuff", "Bribing": "gold_armor",
    "Catalytic": "proc_double", "Priming": "proc_boost", "Empowering": "proc_reroll", "Bolstering": "start_power_boost",
    "Concentrated": "potion_double", "Refined": "potion_boost",
    "Alchemical": "potion_block", "Fermented": "potion_strength", "Distilled": "potion_dexterity",
    "Corrosive": "potion_vulnerable", "Diluting": "potion_weak", "Effervescent": "potion_buffer",
    "Cursefed": "curse_draw_stat", "Attuned": "block_per_relic", "Cursebound": "curse_count_ramp",
    "Quenched": "vigor_strength_pct", "Bracing": "vigor_block_pct", "Rending": "vigor_vulnerable_pct", "Withering": "vigor_weak_pct",
    "Stoking": "vigor_energy_pct", "Wellspring": "vigor_regen_pct", "Surging": "vigor_gain_amplify",
    "Congealing": "vigor_unused_to_block", "Evolving": "vigor_unused_strength_pct", "Frenzied": "vigor_proc_double",
    "Cindered": "exhaust_block", "Bloodforged": "first_hp_loss_strength", "Gouging": "vulnerable_extra_chance", "Retaliating": "hp_lost_vigor",
    "Envenomed": "poison_extra_chance", "Flurrying": "shiv_duplicate_chance", "Cycling": "discard_draw", "Retrieving": "shiv_return_chance",
    "Focused": "first_focus_bonus", "Amplified": "orb_channel_chance", "Supercharged": "focus_when_full", "Preheated": "start_channel_orb",
    "Necromantic": "summon_block", "Dooming": "doom_extra", "Bonebound": "summon_each_turn", "Undying": "doom_defer",
    "Vengeful": "turn_summon_from_hp_lost", "Empathic": "turn_block_from_summon_hp",
    "Starlit": "star_each_turn", "Reforging": "forge_star", "Regal": "star_refund_chance", "Tributary": "forge_draw_chance", "Bountiful": "star_gain_chance",
}
# prefixes with no tree equivalent, and why
DROPPED = {
    "Forging": "Re-forges other relics when you obtain a relic; the imbue/reforge system is removed; the tree replaces it and this mod is Relic Forge's successor (decided).",
}

# --------------------------------------------------------------------------------------------------------------

def parse_old_table(path):
    src = open(path, encoding="utf-8").read()
    body = src[src.index("public static readonly Prefix[] All"):]
    rows, i = [], 0
    while True:
        j = body.find("new Prefix", i)
        if j < 0: break
        k = body.index("{", j)
        depth, m = 0, k
        while True:
            c = body[m]
            depth += (c == "{") - (c == "}")
            if depth == 0: break
            m += 1
        blk = re.sub(r"//[^\n]*", "", body[k + 1:m])
        d = {mm.group(1): mm.group(2) for mm in re.finditer(r'(\w+)\s*=\s*("(?:[^"\\]|\\.)*"|typeof\([^)]*\)|[-\w.]+)', blk)}
        if "Name" in d: rows.append(d)
        i = m
    return rows

def main(table_path, root):
    rows = parse_old_table(table_path)
    prefixes, unclassified = [], []
    for r in rows:
        name = r["Name"].strip('"')
        note = r.get("NoteEn", "").strip('"')
        rec = dict(name=name, note=note)
        if "Penalty" in r:
            rec.update(disposition="drop", family="curse", why="Pure downside (curse). Curses belonged to reforge/shop; the tree is a voluntary choice of boons.")
        elif "PowerPct" in r:
            rec.update(disposition="drop", family="magnitude", why="Scales a host relic's numbers. There is no host relic in the tree.")
        elif name in DROPPED:
            rec.update(disposition="drop", family="relic-system", why=DROPPED[name])
        elif name in NAMES:
            eid = NAMES[name]
            fam = ("character" if "RequiredCharacter" in r else
                   "companion" if "CompanionRelic" in r or "DelayTurn" in r else
                   "fallback" if "FallbackStat" in r else
                   "gamble/other")
            rec.update(disposition=EFFECTS[eid]["status"], effect=eid, family=fam)
            EFFECTS[eid]["sources"].append(name)
        else:
            unclassified.append(name)
        prefixes.append(rec)

    if unclassified:
        sys.exit("UNCLASSIFIED prefixes (add them to NAMES or DROPPED): " + ", ".join(unclassified))
    unused = [e for e, v in EFFECTS.items() if not v["sources"] and not v["new"]]
    if unused:
        sys.exit("effects with no source prefix: " + ", ".join(unused))

    # ---- companion prefixes were hidden-relic grafts; flag that they need native reimplementation ----
    donors = {r["Name"].strip('"'): r["CompanionRelic"] for r in rows if "CompanionRelic" in r}
    for name, d in donors.items():
        EFFECTS[NAMES[name]].setdefault("donorRelics", []).append(d.replace("typeof(", "").rstrip(")"))

    # ---- outputs ----
    import os
    os.makedirs(f"{root}/data/audit", exist_ok=True)
    json.dump(dict(prefixes=prefixes, effects=EFFECTS), open(f"{root}/data/audit/audit.json", "w", encoding="utf-8"), indent=2, ensure_ascii=False)

    cat = dict(schemaVersion=2, effects=[])
    for eid, v in EFFECTS.items():
        e = dict(id=eid, scope=v["scope"])
        if v["cap"] is not None: e["cap"] = v["cap"]
        if v["keywords"]: e["keywords"] = v["keywords"]
        if v["characters"]: e["characters"] = v["characters"]
        cat["effects"].append(e)
    json.dump(cat, open(f"{root}/data/audit/effects.draft.json", "w", encoding="utf-8"), indent=2)

    write_markdown(root, prefixes, rows)
    print(f"{len(prefixes)} prefixes -> {len(EFFECTS)} effects; wrote data/audit and docs/prefix-audit.md")

def write_markdown(root, prefixes, rows):
    from collections import Counter
    disp = Counter(p["disposition"] for p in prefixes)
    fam_drop = Counter(p["family"] for p in prefixes if p["disposition"] == "drop")
    eff_status = Counter(v["status"] for v in EFFECTS.values() if not v["new"])   # ported effects only; the new-design ones are counted separately
    scope = Counter(v["scope"] for v in EFFECTS.values())
    gaps = sorted({n for v in EFFECTS.values() for n in [v["note"]] if "Keyword gap" in n})
    L = []
    w = L.append
    w("# Prefix audit (design doc build step 1)\n")
    w("Generated by `tools/audit/build_audit.py` from the old Relic Forge `PrefixTable.cs` (every entry parsed; the script "
      "fails if any prefix lacks a disposition). Data: `data/audit/audit.json` (everything) and `data/audit/effects.draft.json` "
      "(catalog for `perktree validate`).\n")
    w("## Result\n")
    n_new = sum(1 for v in EFFECTS.values() if v["new"])
    w(f"- **{len(prefixes)} prefixes** in the old table -> **{len(EFFECTS) - n_new} tree effects**, plus **{n_new} new-design effects** = **{len(EFFECTS)}** in the catalog.")
    w(f"- Dropped: **{disp['drop']}** ({', '.join(f'{v} {k}' for k, v in fam_drop.items())}).")
    w(f"- Kept: **{len(prefixes) - disp['drop']}** prefixes merged into {len(EFFECTS) - n_new} effects "
      f"({eff_status['port']} port{', ' + str(eff_status['rework']) + ' need a behavior change first' if eff_status['rework'] else ', none left needing a redesign'}).")
    w(f"- Scope split across all {len(EFFECTS)} effects: {scope['self']} self, {scope['enemyThroughMe']} enemy-through-me, {scope['sharedEnemy']} shared-enemy.\n")
    w("The published `PREFIXES.en.md` is out of date: the code has many families the guide never lists "
      "(potion-use, Vigor, kill/shop, low-HP) and Ironclad's four character prefixes.\n")
    w("## Scope rule used\n")
    w("Design doc section 3, applied like this. When an effect touches several, the **widest wins** "
      "(self < enemyThroughMe < sharedEnemy), because the tooltip label exists to warn about the widest reach.\n")
    w("- **self**: only the owner's own state (powers on self, Block, draw, Energy, gold, HP, own cards).")
    w("- **enemyThroughMe**: only changes what the owner deals to enemies (bonus damage, damage pings).")
    w("- **sharedEnemy**: leaves state on enemies that every player's attacks interact with (Vulnerable/Weak/Poison/Doom, "
      "stripping or spreading enemy powers).\n")
    w("## Findings that change the plan\n")
    n_donors = sum(len(v.get("donorRelics", [])) for v in EFFECTS.values())
    w(f"1. **{n_donors} companion prefixes were hidden-relic grafts** (a donor relic granted silently so its hooks fire). "
      "The design forbids granting relics, so each must be **reimplemented** as host hooks. The donors are listed per effect below; "
      "three more (Mighty, Intimidating, Supple) were fixed effects applied on a later turn. This is the main cost in build step 3.")
    harm = [e for e, v in EFFECTS.items() if v["hook"].startswith("Harmony")]
    sites = sorted({re.search(r"on ([A-Za-z]+\.[A-Za-z]+)", EFFECTS[e]["hook"]).group(1) for e in harm})
    w(f"2. **The old mod is all Harmony patches on the static `Hook` class; most of that is unnecessary.** Every model hook this audit names was checked against the real "
      f"`AbstractModel` and exists. {len(harm)} effects ({', '.join(harm)}) need Harmony, at {len(sites)} sites: {', '.join(f'`{x}`' for x in sites)}. "
      "`CardCmd.Exhaust`, `PlayerCmd.GainEnergy` and `DoomPower.DoomKill` fire on discrete game events. The three resist patches run per damage/block calculation (including card previews), not per frame, and bail out on their first line "
      "for anyone without the perk (see Ground rules). The whole Vigor family looked like it needed a Vigor patch, but the old code detects consumption as a negative-amount "
      "`AfterPowerAmountChanged` on `VigorPower`, which is a plain model hook.")
    w("3. **Wording and plumbing, not redesign.** The three proc auras (`proc_*`) and the effects that boost other perks need the new `PerkProc` plumbing "
      "and relic -> perk rewording; `block_per_relic` now counts relics carried; `gain_amplify` lost its old cost (a bundled enemy-rider curse), which is a balance call left to you.")
    w("4. **Three effects could hit teammates** (design category 4): `symmetric_vulnerable`, `symmetric_weak` and `random_debuff` picked a random *player* from the whole party "
      "(`PickOne(cs.PlayerCreatures, ...)`, confirmed in the old code) while crediting the owner as the source. **Decided: owner-only.** Solo play is unchanged.")
    w("5. **Several 'delay' and 'reduced x0.35' mechanics were balance workarounds for relics** (Mighty/Supple/Intimidating applied late "
      "because a min-1 effect could not be scaled down). With per-node magnitudes they are unnecessary, so they merge into the plain combat-start effects.")
    w("6. **The old RNG was seeded from the relic id** (`RngCompat.Create(seed + turn * ... + hash(relic id))`) and many effects ran as detached "
      "`TaskHelper.RunSafely` tasks. The new host must seed per player and perk, and await effects in order (the old code's own comments record a co-op desync "
      "from a detached Max HP loss).")
    w("7. **Old numbers are placeholders.** `unit` below is the old prefix's number, kept as a starting per-node magnitude; none of it has been rebalanced "
      "against the ~35-45 point budget.\n")
    w("## Decisions made\n")
    w("- **Magnitude prefixes dropped** (Legendary ... Broken, Volatile).")
    w("- **Gamble effects with a built-in cost are keystone-tier nodes**: " + ", ".join(f"`{e}`" for e, v in EFFECTS.items() if v["tier"] == "keystone" and e != "doom_defer") + ".")
    w("- **`curse_count_ramp` counts Curse cards in the deck** (not Relic Forge relic curses).")
    w("- **`doom_defer` (Undying) is a keystone**, placed about 30 allocated points from a start node.")
    w("- **`enemy_strip` (Eroding) is a keystone.** `symmetric_*` and `random_debuff` stay regular nodes, retargeted to the owner only.")
    w("- **Branches:** Frenzied (`vigor_proc_double`) and the rest of the Vigor family go to a Vigor branch in the Regent tree; Eroding stays in Gamble; Resonant "
      "(`gain_amplify`) goes to a strength-and-defense branch. Delegated to me and decided: **Discharging** (`energy_discharge`) -> Gamble, as the energy payoff; "
      "**Obstinate** (`loss_invert`) -> strength and defense, with Resonant.")
    w("- **`forge_draw_chance` (Tributary) is one chance:** +50% to draw 1 card per node. The old second chance (+25% to draw 2) is gone; more nodes of the same effect can reproduce it later if wanted.")
    w("- **Relics: the imbue system is removed, and this mod is Relic Forge's successor.** Relics are no longer a delivery route for any of these effects: the mod adds no relics, grants none, and "
      "leaves the relic pool and reward tables as the game has them. It replaces Relic Forge, so the two are not meant to run together.")
    w("- **Chance effects are not capped at 100%.** Every perk of a chance effect rolls on its own (three +50% perks are three rolls at 50%), and a chance past 100% "
      "(from one big perk, or from `proc_boost` / `proc_double`) gives its whole part as guaranteed procs plus a roll for the rest. Only on/off effects and resistances keep a cap. "
      "Infinite scaling is accepted; the code only caps procs per event at 100 so a runaway stack cannot become an endless loop.")
    w("- **Branch names are internal only.** Branches keep an internal id and name for data and tooling, but the tree UI shows no explicit branch titles.")
    w("- **Balance is yours.** The per-node numbers are the old prefix numbers and are not rebalanced.\n")
    w("## Ground rules\n")
    w("- **Performance:** Harmony patches are fine if they are cheap. No patch on a per-frame method (`Update`, `_Process`, `_PhysicsProcess` and the like) without asking you first. "
      f"Every Harmony site in this project today ({', '.join(f'`{x}`' for x in sites)}, and the mod's `NMapScreen.SetTravelEnabled`, `OnPlayerVoteChanged`, "
      "`OnMapPointSelectedLocally` and `DevConsole` constructor) fires on a discrete event or a damage/block calculation, none per frame. Patches must exit immediately when the player has no relevant perk.")
    w("- **Open follow-ups:** whether other characters get a Vigor section, and the keyword gaps, all below.\n")
    def members(b): return [e for e, v in EFFECTS.items() if v.get("branch") == b]
    def fmt(e): return f"`{e}`" + (" (keystone)" if EFFECTS[e]["tier"] == "keystone" else "")
    luck = ("proc_double", "proc_boost", "proc_reroll")
    mitig = ("vulnerable_resist", "weak_resist", "frail_resist")
    gam = members("gamble")
    costs = [e for e in gam if e not in luck + mitig + ("energy_discharge",)]
    w("## Branches (proposed)\n")
    w("A branch is a tree-layout idea. Each effect can carry a proposed `branch` tag in `audit.json`; only the effects you have named or decided are tagged so far, "
      "and none of this is enforced anywhere.\n")
    w(f"### Gamble (general branch, works on any character): {len(gam)} effects\n")
    w("Instead of scattering the gambles, one branch gives them a shape: cost nodes lead to payoff nodes, with keystones at the far end.\n")
    w("- **Costs and trades** (pay something now for power): " + ", ".join(fmt(e) for e in costs) + ".")
    w("- **Energy payoff:** `energy_discharge` (Discharging). The energy keystones grant bonus Energy every turn, so it fires every turn off them, and it also works with any other bonus Energy.")
    w("- **Luck modifiers** (make chance-based perks anywhere in the tree stronger): " + ", ".join(fmt(e) for e in luck) + ".")
    w("- **Mitigation** (new design): " + ", ".join(fmt(e) for e in mitig) + ". Each makes the matching debuff N points less effective on you, so the self-debuff gambles "
      "(Exposing, Enervating, Chaotic, Echoing) get cheaper the deeper you go. Place them past the cost nodes: tree adjacency then makes the player pay before they profit.\n")
    vig = members("vigor")
    w(f"### Vigor (proposed home: the Regent tree): {len(vig)} effects\n")
    w("Checked against the game: Vigor comes from the cards Patter and Terraforming (both **Regent**), Prep Time (**colorless**, any character), the relic Akabeko, and a monster. "
      "So Regent is the native Vigor character and a Regent branch fits. Members: " + ", ".join(fmt(e) for e in vig) + ". "
      "`vigor_proc_double` (Frenzied) lives here instead of the Gamble branch because it does nothing without the Vigor-consume perks.\n")
    w("Consequence to decide: the old Vigor perks worked for every character. In a Regent-only branch the other characters lose them, unless their trees get a small Vigor section too. "
      "Ironclad's own `hp_lost_vigor` (Retaliating) produces Vigor, so an Ironclad Vigor section would have an obvious reason to exist. `hp_lost_vigor` is not tagged.\n")
    sd = members("strength_defense")
    w(f"### Strength and defense (name open): {len(sd)} effects so far\n")
    w("Tagged so far: " + ", ".join(fmt(e) for e in sd) + ". Both react to Strength/Dexterity changes. "
      "Natural further members (not tagged yet, say if you want a proposed list): the combat-start and per-turn Strength, Dexterity, Block, Thorns, Artifact, Buffer, Blur and Regen effects.\n")
    w("## Keyword gaps\n")
    w("The validator's native list has powers, `CardKeyword` and `StaticHoverTip` ids. These character concepts are not in it and are left out of `keywords` "
      "until the UI recon finds their tooltip source: **Shiv** (a card, `HoverTipFactory.FromCard`), **Orb** (`HoverTip(OrbModel, ...)`), **Stars**, **Osty**. "
      "Effects affected carry 'Keyword gap' in their note.\n")
    w("## Effects\n")
    w("`scope`: S=self, E=enemyThroughMe, X=sharedEnemy. Status: port, or rework (a real behavior change is needed first). The hook column is a **proposal** matched from each effect's trigger: "
      "I checked that every named hook exists on `AbstractModel`, not yet that each passes the data its effect needs (that is part of writing the handler). "
      "'Harmony' in that column means no model hook exists.\n")
    w("| Effect | Scope | Per-node unit (old number) | Proposed hook | Status | Tier | Old prefixes | Notes |")
    w("|---|:-:|---|---|:-:|:-:|---|---|")
    short = {SELF: "S", ETM: "E", SHARED: "X"}
    for eid, v in EFFECTS.items():
        chars = f" ({'/'.join(c.title() for c in v['characters'])})" if v["characters"] else ""
        note = v["note"]
        if v.get("donorRelics"): note = (note + " " if note else "") + "Was a graft of: " + ", ".join(v["donorRelics"]) + "."
        tier = v["tier"] or ""
        if v.get("placement"): note = (note + " " if note else "") + "Placement: " + v["placement"] + "."
        w(f"| `{eid}`{chars} | {short[v['scope']]} | {v['unit']} | {v['hook']} | {v['status']} | {tier} | {', '.join(v['sources']) if v['sources'] else '(new design)'} | {note} |")
    w("\n## Dropped prefixes\n")
    for fam in ("magnitude", "relic-system", "curse"):
        names = [p["name"] for p in prefixes if p["disposition"] == "drop" and p["family"] == fam]
        if not names: continue
        why = next(p["why"] for p in prefixes if p["family"] == fam and p["disposition"] == "drop")
        w(f"- **{fam}** ({len(names)}): {', '.join(names)}.  \n  {why}")
    open(f"{root}/docs/prefix-audit.md", "w", encoding="utf-8").write("\n".join(L) + "\n")

if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
