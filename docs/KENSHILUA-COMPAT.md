# KenshiLua Compatibility Layer (Milestone 6 — `0.10.0`; retired on Meitou 0.24.0+ since `0.12.0`)

> **Retired on Meitou 0.24.0 and later.** Meitou now runs KenshiLua mods itself: Meitou.Lua, from PZA's
> [VlcekM/MeitouClient#1494](https://github.com/VlcekM/MeitouClient/pull/1494) and [#1537](https://github.com/VlcekM/MeitouClient/pull/1537).
> There this layer never starts (`/kl` only lists and stops). It stays, frozen, for Meitou 0.20.1 to 0.23.x; no new work goes into it.
> Since 0.12.0 its save bag is kept per save (shared pack store, key `kl:<mod>`), refreshed once a game hour and on stop.

Author: **PZA** | Licence: **GPLv3 or later** | Module: `mod/src/KenshiLua/`

MeitouMoonscript includes a clean-room, sandboxed **KenshiLua compatibility host** (`KenshiLuaHost`, `KenshiLuaApi`, `MyGuiShim`) designed to run `<Mod>/scripts/init/*.lua` mods on Meitou and serve as a clean upstreamable foundation for Meitou itself.

---

## Automatic Startup (`data/mods.cfg`) & Console Controls (`Shift+Y`)

Any discovered `KenshiLua` mod (local `mods/` or Steam Workshop `workshop/content/233860`) whose `.mod` file is **enabled in `data/mods.cfg`** starts automatically on launch (toggleable via **Settings → Mods → Auto-start KenshiLua mods** or `/config kenshilua.autostart`).

Disabled/unlisted mods stay stopped until manually started via the console:

```text
/kl list                 List discovered KenshiLua mods and their status
/kl allow <mod>          Approve a mod (persisted in kenshilua.grants.json)
/kl start <mod>          Start the mod in its own isolated sandboxed LuaBox
/kl stop <mod>           Stop the mod and clean up all MyGUI shim widgets
/kl revoke <mod>         Revoke approval and stop the mod
```

---

## Implemented Parity Matrix (Milestone 3 — `0.7.2`)

| Surface | Status | Details |
| :--- | :--- | :--- |
| **Mod Discovery** | Implemented | Scans `<mods>/<AnyMod>/scripts/init/*.lua` **and** Steam Workshop `workshop/content/233860/<id>/scripts/init/*.lua` read-only (rejects symlinks/junctions via `SafePath`). |
| **Scoped `require()`** | Implemented | Resolves modules strictly inside `<Mod>/scripts/` and `<Mod>/lib/` (path traversal refused). |
| **Event Registration** | Implemented | `registerHandler(ev, fn)`, `unregisterHandler(id)`, `Events.on/off`. Dispatches `MyGUI::Gui::eventFrameStart`, `onCharsUpdate`, `onCombatHit`, `onCharacterDown`, `onHour`, `onDay`, `onBuildingLoaded`, `onCharacterSelect`, `onKeyDown`, `onPlayerSerialise`, `onPlayerLoadFromSerialise`. |
| **`getGameWorld()` & `getPlayerInterface()`** | Implemented | `activeMods`, `player` / `getPlayerInterface()` (`getCamera()`, `isEnemy(c)`, `getFaction().relations`, `getSelectedCharacters()`), `gamedata:getDataByName()`, `zoneMgr:getZone()`, `getTimeStamp_inGameHours()`, `getCharacterUpdateList()`, `getBuildings()` / `getDoors()`, `getObjectsWithinSphere()`, `getCamera()`. |
| **`Character`, `Handle`, `Medical`, `lektor`** | Implemented | `c.handle` (`toString()`, `getCharacter()`), `lektor.new("hand")`, `c.medical` / `getMedical()` (`anatomy` per-part `flesh`/`fleshStun`/`maxHealth()`/`fatal`, `blood`, `getMaxBlood()`, `stats._toughness`, `unconcious`, `dead`), `crimes:getTotalBounty()`, `isATrader()`, `isIndoors()`, `isUnique()`, `getInventory()`, `giveItem()`, `removeItem()`. |
| **`GameData` Save Bag** | Implemented | `onPlayerSerialise(player, gameData)` / `onPlayerLoadFromSerialise(player, gameData)` with `:getString/:setString/:getNumber/:setNumber/:getBool/:setBool`, kept per save in the save's Moonscript data (key `kl:<mod>`, 256 KB cap; per install in `pack_states/` before 0.12.0). |
| **`Building` / Gate Proxy** | Implemented | `getId()`, `getName()`, `getPos()`, `getFaction()`, `isPlayer()`, `isGate()`, `isOpen()`, `isLocked()`, `isBroken()`, `openDoor()`, `closeDoor()`, `setOpen()`, `setLocked()`, `lockDoor()`, `unlockDoor()`. |
| **`Camera` Proxy** | Implemented | `getPosition()`, `getCameraPos()`, `getCenter()`, `getForward()`, `getFacingDirection()`, `isVisible(pos)`, `getYaw()`, `getPitch()`, `getFov()`, `project(x, y, z)`. |
| **`MyGUI.*` Shim** | Implemented | `getViewSize()`, `RenderManager.getInstance():getViewSize()`, `Align`, `Colour.new(r,g,b,a)` / `Colour(r,g,b,a)`, `createWindow`, `createWidget`, `createTextBox`, `createButton`, `createImageBox` (real `ImageBox` atlas `setImageTexture` + `setImageCoord` + `setColour` + `setAlpha`), `createEditBox`, `:setCaption`, `:setCoord`, `:setVisible`, `:setTextColour`, `:setTextAlign`, `:getTextSize()`, `:destroy`, `:registerCallback`. |
| **Utilities & Lua 5.1 Shims** | Implemented | `KenshiLua.log`, `KenshiLua.version`, `KenshiLua.isMeitou`, `SaveManager.getSingleton()`, `json.encode/decode`, `os.clock()`, mod-scoped read-only `io.open`, `unpack`, `bit` (`bit32`), `itemType`, `SpecialTypes`. |
| **Security & Budgets** | Implemented | Isolated `LuaBox` per mod, global `2,000,000` instructions/tick budget, rate-limited world actions (`16`/tick), no `ffi`, read-only mod-folder-scoped `io.open`. |

---

## Verified Real Workshop Mods (6/6 Pure-Lua Mods)

- **POSH Damage Numbers** (`3821781631`) — loads unmodified, runs `MyGUI::Gui::eventFrameStart` with pure-Lua fallback projection (`"fb"`), renders floating glyph sprites from `DamageNumbers_atlas.png` on body-part damage.
- **POSH Nameplates** (`3822135808`) — loads unmodified, renders WoW-style health/blood bars and icons from `Nameplates_atlas.png` plus character names with live `FactionRelations`.
- **SimpleCompass** (`3776333893`) — loads unmodified, reads `activeMods` path + config JSON, animates sprite-sheet layers via `setImageInfo`/`setImageIndex`.
- **automatic-gates** (`3806949810`) — loads unmodified, tracks player gates via `onBuildingLoaded` + `building:getDoor()` / `getDoorPosition()` / `Faction:isThePlayer()`, and persists gate settings via `onPlayerSerialise` / `onPlayerLoadFromSerialise`.
- **AmputateLimbs** (`3776673278`) — loads unmodified, passes full `RobotLimbs`/`LimbState` environment precheck, registers dialogue conversation handlers cleanly.
- **HappyHerders** (`3801933905`) — loads unmodified, inverts `WaterState`/`GroundType`/`BuildingClassType` enums, runs `onCharsUpdate`/`onCharacterSelect`/`onKeyDown` cleanly.

---

## Upstream Meitou PR Readiness (Remaining Direct-Engine Bindings)

Moving `mod/src/KenshiLua/` into `MeitouClient` itself unlocks the remaining direct-engine bindings that a code mod cannot access through `SlowWorld` snapshots:
- Direct `GameDatabase` (`world.gamedata:getDataByName`), real `MyGuiScreenHost` nested widget trees & `.layout` loading, direct camera view/projection matrices, real `onDialogueStartConversation` / `amputate` / `createRandomCharacter` / `getGroundType` simulation calls, and native save-file integration for `onPlayerSerialise` / `onPlayerLoadFromSerialise`.
- Permanently out of scope on Meitou: LuaJIT `ffi` calls into `kenshi_x64.exe` / `OgreMain_x64.dll` and native C++ RE_Kenshi `.dll` plugins (Meitou is a pure C# engine rewrite).
