# Native Perks HUD button

`NPerkTopBarButton` subclasses the game's `NTopBarButton`. Godot.SourceGenerators 4.5.1 is a build-only
dependency that generates the script registration and native callback dispatch for this class.
No extra runtime DLL or PCK is required.

`PerkMapUi.EnsureTopBar` obtains the native Map button's actual `HBoxContainer` parent and inserts one
`PerkTreeButton` immediately before Map. Godot lays out the row, including native controls and other mods'
siblings. The mod does not replace the container, set screen coordinates, move existing buttons into a
private row, or intercept native Map/Deck/Pause actions or hotkeys. Existing map entry hooks and a deferred
native top-bar ready hook provide initialization-order coverage. Repeated calls do not duplicate the button.
There is no documented game API promising compatibility with every other HUD mod; mods replacing the
row with a non-HBox parent would need a separate adapter.

The button inherits native input handling, hover/press sounds and animations, and enabled/disabled tinting.
Its icon has a separate copy of the native HSV material, so animation cannot recolor Map. Available points
use a copy of the deck-count label's native typography and placement; zero hides the badge. The button is
visible during a supported run and enabled when the map is visible outside combat, preserving the previous
Perks entry's access rules. Closing the map or entering combat also hides the tree. No Map keyboard shortcut
is assigned to Perks.

The old floating text button and unused summary panel are removed.

## Artwork

- `mod/assets/ui/perk-menu-v2.png`: active transparent icon with a slightly thicker, darker outline; v1 is preserved.
  The UI displays it 5% larger (75.6 instead of 72 logical pixels in an 80-pixel native slot), with a centered
  animation pivot. Loaded with mipmaps at HUD size.
- `mod/assets/ui/PROMPT.md`: exact prompt and generation method (built-in image generation).
- Deployment: `<game>/mods/PerkTree/ui/perk-menu-v2.png`, copied by the mod project alongside its DLL output.

## Verification

Build against the installed v0.111.0 / Godot 4.5.1 assemblies. The separate `verification/TopBarSmoke` helper
is a temporary native mod used only for a headless smoke check. It creates an isolated native row at the
main menu, exercises real Godot registration and `_Ready`, checks insertion order/idempotence, the generated
texture and independent shader, and verifies the button is disabled without a run. It does not start a run
or allocate points. Its temporary installation must be removed after the check. Interactive click behavior,
visual scale/placement and third-party HUD mods still need an in-game check by the user; no computer vision
is used.

2026-11-04 results: release build passed; headless smoke reported PASS for registration/`_Ready`, native-row
insertion, idempotence, order, texture loading, independent shader and disabled availability without a run.
The initial helper's unattached-player deck template logged a native exit notification error during cleanup;
the helper now detaches the Map/Deck construction templates before entering the scene tree. No custom-button
exception was logged. The temporary verification mod was removed after the check. Deployed DLL and PNG hashes
match the build outputs.
