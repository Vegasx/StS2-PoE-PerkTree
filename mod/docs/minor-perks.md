# Repeatable minor perks

All six are additive, self-scoped general perks, available in the editor's picker and Balance tab. Each allocated node contributes its own amount. No existing tree nodes were moved, replaced or added.

| Name | Effect ID | Default per node |
| --- | --- | --- |
| Steady | `first_skill_block` | First Skill played each turn: gain 1 Block. |
| Kindled | `first_exhaust_vigor` | First card Exhausted each turn: gain 1 Vigor. Includes Ethereal. |
| Guarded | `first_debuff_block` | First debuff you apply to an enemy each turn: gain 1 Block. |
| Patient | `end_turn_energy_block` | End your turn with unspent Energy: gain 1 Block, regardless of Energy remaining. |
| Restful | `rest_heal_bonus` | Campfire Rest restores 2 additional HP. |
| Provisioned | `shop_gold` | Enter a shop: gain 5 Gold. |

Steady, Kindled and Guarded consume their trigger on the first eligible event even when custom chance rolls fail. Their flags reset before the owner's turn-start draw and other turn-start events, and at combat start. Nodes roll individually and their successful amounts are combined into one grant. Guarded counts negative Strength/Dexterity changes as debuffs, but ignores debuff removal, buffs and another player's applications. Its trigger is enemy-facing; self-inflicted debuffs do not count.

Patient uses the existing end-turn hook before Energy is reset; it does not spend Energy. The host filters turn-end hooks to owners participating in that turn, including in co-op extra turns.

Restful uses the game's native `ModifyRestSiteHealAmount` hook, so the Rest preview and actual healing agree, other Rest modifiers still apply, and the native HP cap remains authoritative. It does not affect other sources of healing or campfire options. It is guaranteed only: previews must not roll RNG. Its chance field is locked by the existing editor support mechanism.

Provisioned uses `AfterRoomEntered` for a `MerchantRoom`, only for a living owner. `shop_gold.paid_floor` is saved in the existing perk counters alongside the native Gold save. Duplicate room notifications and reloading a paid shop cannot pay again. A failed custom chance roll also consumes that shop floor's trigger.

## Verification

Build the mod against the installed game, then run the regression harness:

```powershell
dotnet build mod/src/PerkTree.Mod -c Release '-p:Sts2LibDir=C:\SteamLibrary\steamapps\common\Slay the Spire 2\data_sts2_windows_x86_64' --no-restore
dotnet run --project mod/verification/MinorPerkCheck -c Release
dotnet test mod/tests/PerkTree.Core.Tests -c Release --no-restore
node tools/scan-mod.mjs
node tools/validate.mjs
python mod/tools/check_handlers.py
```

The harness runs the compiled host/handlers against installed game types with animation/command boundaries intercepted. It exercises three-node stacking, pending and foreign-player filtering, turn resets, debuff attribution, negative Strength, Energy conditions, repeatable Rest previews and shop payouts across perk save/restore. It does not establish live UI, gameplay or co-op acceptance.

The shipped runtime catalogs include the six new effects and their tooltip/icon metadata. Existing runtime effect values and tree snapshots were preserved because the editor data already contained separate, unexported balance/layout changes. A future full `node tools/export-mod.mjs` exports the current editor state as usual.
