# KenshiLua Compatibility Layer (Milestone 3 — `0.7.3`)

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
| **`GameData` Save Bag** | Implemented | `onPlayerSerialise(player, gameData)` / `onPlayerLoadFromSerialise(player, gameData)` with `:getString/:setString/:getNumber/:setNumber/:getBool/:setBool`, persisted to `pack_states/kl_<mod>.json` (256 KB cap). |
| **`Building` / Gate Proxy** | Implemented | `getId()`, `getName()`, `getPos()`, `getFaction()`, `isPlayer()`, `isGate()`, `isOpen()`, `isLocked()`, `isBroken()`, `openDoor()`, `closeDoor()`, `setOpen()`, `setLocked()`, `lockDoor()`, `unlockDoor()`. |
| **`Camera` Proxy** | Implemented | `getPosition()`, `getCameraPos()`, `getCenter()`, `getForward()`, `getFacingDirection()`, `isVisible(pos)`, `getYaw()`, `getPitch()`, `getFov()`, `project(x, y, z)`. |
| **`MyGUI.*` Shim** | Implemented | `getViewSize()`, `RenderManager.getInstance():getViewSize()`, `Align`, `Colour.new(r,g,b,a)` / `Colour(r,g,b,a)`, `createWindow`, `createWidget`, `createTextBox`, `createButton`, `createImageBox` (real `ImageBox` atlas `setImageTexture` + `setImageCoord` + `setColour` + `setAlpha`), `createEditBox`, `:setCaption`, `:setCoord`, `:setVisible`, `:setTextColour`, `:setTextAlign`, `:getTextSize()`, `:destroy`, `:registerCallback`. |
| **Utilities & Lua 5.1 Shims** | Implemented | `KenshiLua.log`, `KenshiLua.version`, `KenshiLua.isMeitou`, `SaveManager.getSingleton()`, `json.encode/decode`, `os.clock()`, mod-scoped read-only `io.open`, `unpack`, `bit` (`bit32`), `itemType`, `SpecialTypes`. |
| **Security & Budgets** | Implemented | Isolated `LuaBox` per mod, global `2,000,000` instructions/tick budget, rate-limited world actions (`16`/tick), no `ffi`, read-only mod-folder-scoped `io.open`. |

---

## Verified Real Workshop Mods

- **POSH Damage Numbers** (`3821781631`) — loads unmodified, runs `MyGUI::Gui::eventFrameStart` with pure-Lua fallback projection (`"fb"`), renders floating glyph sprites from `DamageNumbers_atlas.png` on body-part damage.
- **POSH Nameplates** (`3822135808`) — loads unmodified, renders WoW-style health/blood bars and icons from `Nameplates_atlas.png` plus character names.

---

## Next Milestone (Upstream Meitou Integration)

- Direct Meitou upstream PR integration: first-class `GameWorld` pause/speed/spawn/dialogue bindings (`DialogueScriptingBridge` / `WorldStateScriptingBridge`), native `onKeyDown` hook from Meitou's input router, and optional LuaJIT/5.1 compat discussion with upstream maintainers.
