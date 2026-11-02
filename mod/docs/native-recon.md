# Native recon: what the game gives us (design doc build step 2, part 1)

Source: metadata of `sts2.dll` (game **v0.111.0**, Aug 2026 build, early access) and `0Harmony.dll` 2.4.2, plus the
game's own XML doc file `data_sts2_windows_x86_64/sts2.xml` (20,722 documented members; search it before guessing).
This is names, signatures, doc comments and a call-site scan of the IL, **not** decompiled method bodies, so behavior
is inferred unless it says "IL-verified" or "doc-confirmed". Everything marked *unverified* needs an in-game test.

## 1. Loading mods: BepInEx is not needed

- The game has its own loader: `ModManager` reads a manifest (`ModManifest`: id, name, author, description,
  version, hasPck, hasDll, dependencies, affectsGameplay, minGameVersion) and calls the method named by
  `[ModInitializer("MethodName")]` (`ModManager.TryLoadMod` -> `CallModInitializer`, IL-verified).
- `sts2.dll` itself references **0Harmony 2.4.2** and calls `ModManager.HasHarmonyPatches()` (IL-verified, used by
  its error reporting / feedback screen to flag modded sessions). Harmony ships with the game.
- So: use the native loader + Harmony for patches. BepInEx would be a second loader and an extra dependency for
  players, with nothing it adds that we need.
- **Mod package layout** (from the installed example mods): `mods/<Id>/<Id>.json` (manifest) + `<Id>.dll`
  (+ optional `<Id>.pck`). Manifest keys: `id, name, author, description, version, has_pck, has_dll, dependencies,
  affects_gameplay`. The doc comment on `ModInitializerAttribute`: with the attribute the game calls your method and
  does **not** auto-patch; without it the game runs `Harmony.PatchAll` for you.
- **Co-op:** `ModManager.GetGameplayRelevantModNameList` compares gameplay mods between players, driven by the
  manifest's `affects_gameplay`. Set it true so both players must have the mod.
- Build target facts: `sts2.dll` references **.NET 9** and **GodotSharp 4.5.1** (also SmartFormat 3, Steamworks.NET,
  Sentry). Compiling against it needs `GodotSharp.dll` at minimum.

## 2. Effect host: no relic and no hidden power needed (best option)

`ModHelper.SubscribeForRunStateHooks(string, RunHookSubscriptionDelegate)` and `SubscribeForCombatStateHooks(...)`
take a delegate that returns `IEnumerable<AbstractModel>`. `RunState.IterateHookListeners` and
`CombatState.IterateHookListeners` call into those subscriber lists (IL-verified). `AbstractModel` has ~183
overridable hook methods (`BeforeCombatStart`, `AfterCombatEnd`, `AfterCardPlayed`, `AfterPlayerTurnStartEarly`,
`AfterDamageReceived`, `BeforeHandDraw`, `AfterGoldGained`, `AfterRoomEntered`, `AfterActEntered`, ...).

**Plan:** one `PerkHostModel : AbstractModel` per player, returned from a run-state subscription. It receives the
game's normal hooks for the whole run, never appears in the relic bar, and doesn't touch other mods' relic logic
(the compatibility goal from design doc section 10).

Fallback if that fails in game: a hidden power (`PowerModel.IsVisible` exists). Weaker, because powers live on a
creature and vanish between combats.

Doc-confirmed: `SubscribeForRunStateHooks` is documented as the way for mods to "provide custom model types to a
RunState when IterateHookListeners is called". `AfterRoomEntered` is documented as the hook for start-of-combat powers
("use this hook with a `room is CombatRoom` check") and is delivered to all models, in or out of combat.
`AfterCombatVictory` likewise reaches non-combat models.

`InitId` is only called by the game's own `ModelDb.InitIds` for models it registers, and hook iteration doesn't read
`Id`, so the host doesn't need one. The mod sets one best-effort inside try/catch anyway (it consults the game's
id-serialization cache, which threw in an uninitialized test process).

*Unverified:* behavior of a mod-made model in the live game (nothing has run in game yet).

## 3. Map screen and the lock rule

- `NMapScreen` (singleton `Instance`, `IsOpen`, `IsTravelEnabled`, `PlayerVoteDictionary`, `Opened`/`Closed` events).
- Voting: `OnMapPointSelectedLocally`, `OnPlayerVoteChanged(Player, MapVote?, MapVote?)`,
  `OnPlayerVoteCancelled(Player)`, `InitMapVotes`, `TravelToMapCoord`. Actions: `VoteForMapCoordAction`,
  `NetVoteForMapCoordAction`; sync in `MapSelectionSynchronizer`.
- **Votes can be changed and cancelled.** The design says voting locks perks, so the lock must latch on a player's
  *first* vote and must NOT release when they cancel or change it. Otherwise "vote, respec, cancel" is a loophole.
  This matches the doc's intent; it just needs to be explicit in the code.
- Reusable native widgets on this screen for the entry button: `NMapDrawButton`, `NMapEraseButton`,
  `NMapClearButton`, `NMapShareButton`, `NHotkeyIcon`, `NBackButton`.

## 4. Tooltips and keywords (companion tooltips)

- `HoverTip` (struct: Title, Icon, Description, Id, IsSmart, IsDebuff, ...) is built from `LocString`s.
- `HoverTipFactory`: `FromPower(PowerModel, int?)`, `FromKeyword(CardKeyword)`, `Static(StaticHoverTip, DynamicVar[])`,
  `FromRelic`, `FromCard`, `ForEnergy`, ...
- `NHoverTipSet.CreateAndShow(Control owner, IEnumerable<IHoverTip>, HoverTipAlignment)` does placement and stacking.
  So a perk tooltip is just: our own tip + `FromPower(...)` / `FromKeyword(...)` for each keyword the effect
  references. The game's own keyword text is reused, as the design asks.
- Native keyword universe (generated into `data/native-keywords.txt`, 289 ids): 268 power classes (ThornsPower,
  VulnerablePower, WeakPower, PoisonPower, StrengthPower, DexterityPower, FrailPower, ...), `CardKeyword`
  {Exhaust, Ethereal, Innate, Unplayable, Retain, Sly, Eternal}, `StaticHoverTip` {Channeling, Evoke, Transform,
  Block, Fatal, Energy, Stun, CardReward, Forge, SummonDynamic, SummonStatic, ReplayDynamic, ReplayStatic, Cook}.
  `perktree validate --keywords data/native-keywords.txt` checks effect keywords against it.
- **Naming collision:** `Forge` is already a native hover-tip keyword (`StaticHoverTip.Forge`,
  `HoverTipFactory.FromForge`). The doc's name idea "Forge Ledger" for the summary view would sit next to a real
  game mechanic called Forge. Consider another name.

## 5. Still to find (needs more than metadata)

- Harmony 2.4.2 refuses to run on .NET 10 ("CoreCLR version 10.0.14 is not supported"), so patch application can only
  be tested inside the game (it runs .NET 9).
- Localization: `ModManager.GetModdedLocTables(language, fileName)` is documented as returning the loc table files
  supplied by loaded mods, so mods can ship their own tables (e.g. a `cards.json` per language).

## 6. Vulnerable / Weak / Frail internals (for the resistance perks)

Read from `sts2.dll` IL, not guessed:

- **Base values** (each power's `get_CanonicalVars` builds a `DynamicVar` from a decimal constant): Vulnerable **1.5** (the XML docs also say "multiplies damage received by 1.5x"), Weak **0.75**, Frail **0.75** (a constant returned directly).
- **`ModifyDamageMultiplicative` / `ModifyBlockMultiplicative` return a multiplier**, not a new amount: the `AbstractModel` default is `Decimal.One`, and Frail returns 0.75.
- **The game hardcodes its adjusters by name.** `VulnerablePower.ModifyDamageMultiplicative` calls `PaperPhrog`, `CrueltyPower` and `DebilitatePower`; `WeakPower` calls `PaperKrane` and `DebilitatePower`. There is no generic hook, so a mod cannot join that chain; a Harmony postfix on the final result is the exact approach.
- Vulnerable returns neutral (1) unless the damage target is the power's owner and the hit is a powered attack, so a postfix can safely bail when the result is already neutral.

## 7. How the game saves and loads a run (for persistence)

Read from `sts2.dll` metadata and IL, then exercised against a real `RunSaveManager` with an in-memory store:

- **No slot for mod data.** `SerializablePlayer` and `SerializableRun` are fixed schemas, source-generated for JSON (`MegaCritSerializerContext`) and also written as network packets (`IPacketSerializable`). `SerializableExtraPlayerFields` is a handful of fixed ints. So mod data goes in a sidecar file.
- **One write path.** Every run save goes through `RunSaveManager.SaveRun(SerializableRun save, bool isMultiplayer)`, which picks `CurrentRunSavePath` or `CurrentMultiplayerRunSavePath` and writes via `ISaveStore`. `SerializableRun.StartTime` identifies the run.
- **Paths:** `profile<N>/saves/current_run.save` (single-player) and `current_run_mp.save` (multiplayer), built by `RunSaveManager.GetRunSavePath` from `UserDataPathProvider` (which also tracks `IsRunningModded`). `DeleteCurrentRun` deletes the run save and a suffixed sibling file, so a suffix scheme is normal here.
- **When saves happen:** `RunManager.EnterMapPointInternal` (room entry), `CombatManager.EndCombatInternal`, `EventRoom.OnEventStateChanged`, starting a multiplayer run, and the reload-count resave right after a load.
- **Combat end order (IL-verified):** `Hook.AfterCombatEnd` ... `Hook.AfterCombatVictory` ... `CombatRoom.MarkPreFinished` -> `SaveManager.SaveRun` -> `NMapScreen.SetTravelEnabled`. So anything awarded in `AfterCombatVictory` is inside that save, and a reload resumes the already-won (pre-finished) room instead of replaying it.
- **Loading:** `RunState.FromSerializable(SerializableRun)` builds the RunState; `RunManager.State` (private) is the live one.
- **Cleanup:** `DeleteCurrentRun` / `DeleteCurrentMultiplayerRun` are called from `RunManager.OnEnded` and `NMainMenu.AbandonRun`.
- *Unverified:* whether `NMapScreen.SetTravelEnabled(true)` is called again when such a pre-finished run is loaded (it decides whether the allocation window reopens by itself after a reload).


## 8. How native relics implement the effects we mirror (for the effect handlers)

Read from the relics' async state machines in `sts2.dll` IL. Each handler uses the same hook and command shape as the relic it copies.

| Perk effect | Native idiom | Hook | Command shape |
|---|---|---|---|
| `start_thorns` | Bronze Scales | `AfterRoomEntered` (combat room) | `PowerCmd.Apply<ThornsPower>(ctx, me, n, me, null, false)` |
| `start_block` | Anchor | `BeforeCombatStart` | `CreatureCmd.GainBlock(me, n, Unpowered, null, false)` |
| `start_vigor` | Akabeko (`AfterSideTurnStart`, turn 1) | turn 1 of the owner (`AfterPlayerTurnStartLate`) | `PowerCmd.Apply<VigorPower>` |
| `start_heal` | Blood Vial | `AfterPlayerTurnStartLate`, `PlayerCombatState.TurnNumber == 1` | `CreatureCmd.Heal(me, n, true)` |
| `turn_ping_damage` | Mercury Hourglass | `AfterPlayerTurnStart` (we fire it from the `Late` variant with the other turn effects, after the draw) | `CreatureCmd.Damage(ctx, HittableEnemies, n, Unpowered, me)` |
| `end_turn_no_block` | Orichalcum | `BeforeSideTurnEnd` (checks block is 0) | `GainBlock` |
| `energy_per_attacks` | Nunchaku | `AfterCardPlayed`: `card.Owner == Owner`, `Type == Attack`, `CombatManager.IsInProgress` | `PlayerCmd.GainEnergy(n, owner)` |
| `draw_on_first_hit` | Centennial Puzzle | `AfterDamageReceived`: target is owner, `UnblockedDamage > 0`, once per combat | `CardPileCmd.Draw` |

- **`ValueProp` values** (read from the enum constants, correcting an earlier guess): `Unblockable = 2`, `Unpowered = 4`, `Move = 8`, `SkipHurtAnim = 16`. The native
  relics pass **`Unpowered`** (4) for their flat damage and block, so Strength/Dexterity don't scale them. The handlers do the same. `CardType`: Attack 1, Skill 2, Power 3,
  Status 4, Curse 5, Quest 6. `CombatSide`: None 0, Player 1, Enemy 2.
- **`PlayerCombatState.TurnNumber`** is the per-player turn counter Blood Vial and Akabeko use for "turn 1"; it is better than `RoundNumber` for per-player effects.
- **`DamageResult`** has `Receiver`, `Props`, `BlockedDamage`, `UnblockedDamage`, `OverkillDamage`, `TotalDamage`, `WasBlockBroken`, `WasFullyBlocked`, `WasTargetKilled`.
- **RNG for chance effects:** see section 9. In short, perks use their own per-(player, effect, combat) stream built with the game's `Rng(Player, ModelId, ulong)` constructor, not the shared run streams.
- **Unverified:** the last `bool` of `CardPileCmd.Draw` (the handler passes `false`, meaning "not the normal turn draw", from its name only), and that `AfterPlayerTurnStartLate`,
  `BeforeCombatStart` and `BeforeSideTurnEnd` reach a mod-made host as they do a relic (they are `AbstractModel` virtuals, so they should).

## 9. The game's RNG and its multiplayer sync (for chance effects and counters)

Read from `sts2.dll` metadata, IL, string literals and the game's `sts2.xml`:

- **Streams.** `RunState.Rng` is a `RunRngSet`: named `Rng` streams (`UpFront`, `Shuffle`, `UnknownMapPoint`, `CombatCardGeneration`, `CombatPotionGeneration`, `CombatCardSelection`,
  `CombatEnergyCosts`, `CombatTargets`, `MonsterAi`, `Niche`, `CombatOrbs`, `TreasureRoomRelics`) plus a run `Seed`. A `PlayerRngSet` per player has `Rewards`, `Shops`, `Transformations`.
  All are saved with the run (`SerializableRng` = a draw counter + four 64-bit state words).
- **`Rng.Chaotic` is explicitly non-deterministic** ("will not produce the same results after saving and loading", docs). Never use it for anything that touches gameplay.
- **The game's own idiom for "my own deterministic stream"** is `new Rng(Player, ModelId, ulong extra)`. Its documented behavior: unique per run seed, per player *slot index* (not NetId, so
  rejoining can't change it), per content id, and per `extra`. IL: `RunState.Rng.Seed` + `GetPlayerSlotIndex` + `StringHelper.GetDeterministicHashCode(modelId.Entry)`. Native users: Fur Coat, Byrdpip,
  Paels Legion. Map and event synchronizers use `new Rng(seed, "name")`. `GetDeterministicHashCode` exists because `string.GetHashCode` is randomized per process.
- **`Rng.NextInt(max)`** returns [0, max). `NextDouble` is [0, 1).
- **The game re-syncs at every combat start in multiplayer.** `CombatStateSynchronizer` (`StartSync` / `WaitForSync`): every client broadcasts its own `SerializablePlayer`, and the host's `SerializableRunRngSet`
  plus the shared relic grab bag go to the clients, which wait for them ("we never received the RNG set!" otherwise). Consequences:
  - The game's *shared* streams are healed to the host's state at each combat start, but a stream we keep ourselves is not, so ours must be a pure function of synced inputs (it is: see below).
  - This is the game's own "each player is authoritative for their own data, broadcast at combat start" moment, the natural place to also exchange each player's committed perk set. `SerializablePlayer`
    has no spare field, so we send a message or game action of our own, which mods can register (section 10).
- **What the mod does:** one stream per (player, effect, combat), `new Rng(player, ModelId("PERK", effectId), floor * 64 + ordinalOnFloor)`, recreated at `BeforeCombatStart`. Nothing is saved or restored, a reload
  replays a combat with the same rolls, and one combat can't shift the next. Edge case: if you reload between two combats on the SAME floor, the second combat's ordinal restarts at 0 and repeats the first
  combat's key (still identical on every client, just correlated). Outside combat there is no stream; rolls are combat-only.
- *Unverified in game:* that the `Rng(Player, ModelId, ulong)` constructor accepts a made-up `ModelId` category (it only reads `Entry` per the IL), and that `TotalFloor` is already current at `BeforeCombatStart`.

## 10. Can a mod send its own network messages? (for co-op sync of allocations)

Yes, there are two supported routes. Read from IL and string literals; the startup order is IL-verified, nothing has run in game.

- **Mods are scanned for message types.** `MessageTypes.Initialize` builds the wire-id table from the game's own `INetMessageSubtypes` plus `ReflectionHelper.GetSubtypesInMods<INetMessage>()`, which walks every
  type in every loaded mod's assembly. `ActionTypes.Initialize` does the same for `INetAction`. A mod type that implements the interface is registered with no patch needed. Ids come from sorting the type
  names, so every client with the same mod gets the same ids (the manifest's `affects_gameplay` already forces both players to have it).
- **Startup order is safe.** `NGame.GameStartup` runs `ExecuteVeryEarly` (which awaits `ModManager.Initialize`, loading the mods) before `ExecuteEssential` (which builds the message and action tables).
- **Route 1, a plain message.** Implement `INetMessage` (a `struct`, like the game's own) with `Serialize(PacketWriter)` / `Deserialize(PacketReader)` plus `ShouldBroadcast`, `Mode`
  (`NetTransferMode`: None / Unreliable / Reliable), `LogLevel` and `ShouldBuffer`. Send with `RunManager.Instance.NetService.SendMessage(msg)` (all) or `SendMessage(msg, peerId)`;
  receive with `NetService.RegisterMessageHandler<T>((msg, senderId) => ...)`. The host relays a received message to the other clients when `ShouldBroadcast` is true (`NetHostGameService.OnPacketReceived`).
  `ShouldBuffer` holds a message until handlers exist while a game is loading. `PacketWriter` has `WriteBool/Byte/Int/UInt/Long/ULong/String/List` etc.
- **Route 2, a game action (the game's own ordered RPC).** `GameAction` + `INetAction` (`ToGameAction(Player)`), enqueued with `ActionQueueSynchronizer.RequestEnqueue`. The host sequences it and every client
  executes it at the same position in the action queue, exactly how `VoteForMapCoordAction` works. `GameActionType` has `NonCombat` and `Any`, so it can run on the map screen.
- **Why this matters:** the lock-on-vote commit could be a `GameAction` enqueued just before the player's map vote, so every client applies that player's committed nodes at the same queue position, before
  any travel or combat. A plain message is simpler but arrives outside the queue, so the combat start would have to wait until every peer's message has arrived (as `CombatStateSynchronizer.WaitForSync` does).
- *Unverified:* that a mod-defined action or message round-trips in a real two-player session; how to get the live `INetGameService` before a run exists (`RunManager.NetService` is set during run setup);
  handler (un)registration per run (the game's own synchronizers register in their constructors and unregister in `Dispose`).

### What was verified about the commit action (built from the above)

- **The game rebuilds the action on every client from its network form**, the sender's included (`HandleActionEnqueuedMessage` -> `NetActionToGameAction` -> `INetAction.ToGameAction(Player)`); only the host runs the
  original object. So everything the action needs must be in the net struct. `VoteForMapCoordAction` has the same shape (`ToNetAction`, `OwnerId` = the player's NetId, `ActionType` = **Any**), and ours copies it.
- **Run-location tagging.** `RequestEnqueue` stamps the request with `RunLocationTargetedMessageBuffer.CurrentLocation`; a client that has not reached that location yet holds the message until it does
  (`OnLocationChanged`). That covers a slow client receiving the commit early.
- **The local vote is built in one place.** `NMapScreen.OnMapPointSelectedLocally` creates `VoteForMapCoordAction` and calls `RequestEnqueue` itself (IL). A *prefix* there enqueues our commit first. A constructor
  patch on the vote action was rejected: tiny constructors can be inlined past a Harmony patch.
- **Checked in the sandbox against the real DLL:** the game's own mod scanner (`ReflectionHelper.GetSubtypesFromAssembly`) finds `NetCommitPerksAction`, and it round-trips byte-for-byte through the real
  `PacketWriter`/`PacketReader` (empty, normal and long-id cases).
- *Still unverified, needs two real players:* host and client both sequencing and running it, the prefix firing before the vote in solo and co-op, and per-player queue FIFO order between our action and the vote.
