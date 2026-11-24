# MeitouMoonscript Modder Guide

Author: **PZA** | Licence: **GPLv3 or later** | API: **v1**

MeitouMoonscript lets you add sandboxed, deterministic Lua 5.2 scripts to Meitou in two ways:

1. **Zero-C# Lua Packs** — drop a folder into your own Meitou mod at `<YourMod>/moonscript/<pack-id>/` with a `pack.json` and `main.lua`. No Visual Studio, no C# DLL, no compiler.
2. **C# `IScriptHost` Dependency** — reference `pza.moonscript` (`apiVersion: 1`) from your own C# Meitou mod to host Lua brains, minigames, rules engines, or NPC logic inside isolated sandboxes.

---

## 1. Zero-C# Lua Packs

### Folder Layout

Inside any Meitou mod folder (next to `meitou.json`), create a `moonscript/` directory:

```text
mods/
  YourMod/
    meitou.json
    moonscript/
      my-pack/
        pack.json
        main.lua
```

MeitouMoonscript automatically discovers every `pack.json` under `<mods>/<AnyMod>/moonscript/<pack>/`.

### `pack.json` Schema

```json
{
  "id": "my-pack",
  "name": "My Pack",
  "author": "YourName",
  "version": "0.1.0",
  "side": "ui",
  "entry": "main.lua",
  "permissions": ["commands", "world.read"],
  "description": "Short one-line description shown in /packs"
}
```

- `id`: unique lowercase identifier (e.g. `my-pack`).
- `entry`: relative path to your Lua file inside the pack folder (path traversal `..` and symlinks/junctions are rejected).
- `permissions`: optional array of capabilities the pack requests (defaults to `["commands", "timers", "world.read"]`):
  - `"commands"` — `command(name, fn)`
  - `"timers"` — `after(hours, fn)` and `every(hours, fn)`
  - `"events"` — `on(event, fn)`
  - `"world.read"` — `world.hour()`, `world.day()`, `world.cats()`, `world.squad()`, `selected.name()`
  - `"world.scan"` — spatial & outpost sensors: `world.characters([x, z, radius, filter])`, `world.doors([onlyPlayer])`, `world.buildings([onlyPlayer])`
  - `"world.control"` — rate-limited world actuators (max 16 actions/tick per pack, queued onto Meitou's simulation command queue): `doors.*`, `orders.*`, `world.floater(...)`
  - `"pack.save"` — automatic C7-safe persistence of the pack's plain-data `state` table across restarts (`pack.save()`, max 256 KB)

### Pack Lifecycle & Security Model

- **Built-in packs** (`MeitouMoonscript/packs/*`) start automatically when the terminal initializes.
- **Third-party packs** (`<OtherMod>/moonscript/*`) are **discovered** automatically and shown in `/packs` with their source mod (e.g. `(mod:YourMod)`), but **require opt-in permission approval** before starting:
  - `/allow <pack> all` (or `/allow <pack> <permission>`) approves the pack's declared safe permissions and persists the approval in `packs.grants.json`.
  - `/revoke <pack> [permission|all]` revokes permissions and stops the pack.
  - **Least privilege**: `PackHost` only exposes the Lua functions matching the pack's approved permissions (for example, a pack with only `["commands"]` cannot access `world.*` or `on(...)`).
  - **Future privileged permissions** (anything outside `commands`, `timers`, `events`, `world.read`) are refused until explicitly supported by the host.
  - Third-party packs also stay stopped by default unless started with `/start <id>` or `/config packs.autostart true` (and even with autostart on, unapproved packs stay stopped until `/allow`).
- **Command collision safety**: built-in terminal commands (`/help`, `/run`, `/packs`, `/start`, `/stop`, `/restart`, `/config`, `/battle`, `/crawlers`, etc.) are reserved and cannot be overridden. If two packs register the same command name, the first pack gets `/<cmd>` and the second automatically gets `/<pack-id>:<cmd>`.
- **Isolation**: every pack runs in its own sandboxed `LuaBox`. One pack crashing or hitting its instruction budget cannot break another pack or freeze Meitou.
- **Global tick budget**: all running packs share a hard per-frame ceiling (`800,000` Lua instructions per frame) in addition to each pack's individual call budget.

---

## 2. Pack API Reference

Every pack receives these built-in functions and globals:

### Commands, Timers, and Events

| Function | Description |
| :--- | :--- |
| `command(name, fn)` | Register a terminal slash command `/<name>`. `fn(args)` receives the trimmed argument string. |
| `after(hours, fn)` | Schedule `fn()` once after `hours` of in-game clock time (deterministic, pauses when the game pauses). |
| `every(hours, fn)` | Schedule `fn()` repeatedly every `hours` of in-game clock time. |
| `on(event, fn)` | Subscribe to an allow-listed game event. |

#### Allow-Listed Game Events (`on(event, fn)`)

| Event | Payload Fields |
| :--- | :--- |
| `"hour"` | `{ hour = number, day = integer }` |
| `"day"` | `{ day = integer, hour = number }` |
| `"hit"` | `{ attacker, target, damage, cut, blunt, ko, death, ranged }` |
| `"character_down"` | `{ name, attacker, dead }` |

Unknown event names are rejected with an error. Event callbacks run inside the pack's sandbox and share the global per-frame budget.

### Read-Only World Snapshot

| Function | Returns |
| :--- | :--- |
| `world.hour()` | Current in-game hour of day `0.00–23.99` (or `nil` if no world loaded). |
| `world.day()` | Current 1-based day number. |
| `world.cats()` | Player faction cats (integer). |
| `world.squad()` | Array of live squad fighter tables (`name`, `race`, `skeleton`, `hp`, `maxHp`, `bleed`, `down`, `str`, `dex`, `tough`, `atk`, `def`, `dodge`, `ma`, `med`, `robotics`, `weapon`, `weaponDmg`, `ranged`, `tier`, `armour`). |
| `selected.name()` | Name of the currently selected character (or `nil`). |

### World Sensors (`"world.scan"`)

| Function | Returns |
| :--- | :--- |
| `world.characters([x, z, radius, filter])` | Array of active characters (`slot`, `gen`, `name`, `faction`, `is_player`, `x`, `y`, `z`, `dist`, `hp`, `down`, `dead`, `in_combat`, `squad_id`). Optional `filter`: `"player"`, `"npc"`, `"alive"`, `"combat"`. |
| `world.doors([onlyPlayer])` | Array of door/gate fixtures (`id`, `building`, `owner`, `is_player`, `x`, `y`, `z`, `open_amount`, `motion`, `locked`, `broken`, `is_gate`). |
| `world.buildings([onlyPlayer])` | Array of player-built structures (`id`, `name`, `record`, `owner`, `is_player`, `x`, `y`, `z`, `complete`, `progress`). |

### World Actuators (`"world.control"`, max 16 actions/tick per pack)

All actuators enqueue onto Meitou's deterministic `World.Commands` queue on the next simulation tick and return `true` if queued or `false` if the per-tick rate limit was reached:

| Function | Effect |
| :--- | :--- |
| `doors.toggle(buildingId)` | Toggle open/close on a building's doors/gates. |
| `doors.set_open(buildingId, open)` | Idempotent open/close (only toggles if not already in the desired state). |
| `doors.toggle_lock(buildingId)` | Toggle lock state on a building's doors/gates. |
| `doors.set_locked(buildingId, locked)` | Idempotent lock/unlock (only toggles if not already in the desired state). |
| `orders.move(chars, x, y, z)` | Move one character table (`{slot, gen}`) or an array of them to `(x, y, z)`. |
| `orders.stop(chars)` | Stop current movement/orders for the given character(s). |
| `orders.follow(chars, target[, guard])` | Follow (or bodyguard when `guard == true`) `target` (`{slot, gen}`). |
| `orders.attack(chars, target)` | Order character(s) to attack `target` (`{slot, gen}`). |
| `orders.squad_ai(option, on)` | Toggle squad AI flag (`"heal"`, `"help"`, `"rescue"`, `"shoot_first"`, `"heal_neutrals"`, `"heal_all"`, `"ditch"`, `"sleep"`, `"sit"`, `"share"`, `"feed"`). |
| `world.floater(x, y, z, text)` | Show a floating 3D world label at `(x, y, z)` (no console needed). |

### Persistent State (`"pack.save"`)

| Function | Effect |
| :--- | :--- |
| `pack.save()` | Immediately persists the pack's plain-data `state` table (up to 256 KB). Also auto-saved on `/stop` and auto-restored on `/start`. |

### Vetted Pure-Lua Standard Library (`lib`)

All helpers in `lib` are pure Lua 5.2, 100% deterministic, and safe inside the sandbox:

- **`lib.tween`** — 22 easing functions (`linear`, `inQuad`, `outQuad`, `inOutQuad`, `inCubic`, `outCubic`, `inOutCubic`, `inQuart`, `outQuart`, `inOutQuart`, `inSine`, `outSine`, `inOutSine`, `inExpo`, `outExpo`, `inOutExpo`, `inBack`, `outBack`, `inOutBack`, `outBounce`, `inBounce`, `inOutBounce`, `outElastic`):
  - `lib.tween.ease(name, t)`
  - `lib.tween.lerp(a, b, t, easeName)`
  - `local tw = lib.tween.new(duration, subject, target, easeName)` -> `tw:update(dt)`, `tw:set(clock)`, `tw:reset()`, `tw:progress()`
- **`lib.fsm`** — plain-data finite state machine (round-trips cleanly through `Save()` / `Restore()` when stored inside `state`):
  - `local m = lib.fsm.new("idle")`
  - `lib.fsm.is(m, "idle")`
  - `lib.fsm.go(m, "combat")`
  - `lib.fsm.tick(m, dt, { idle = function(m, dt) return "ready" end })`
- **`lib.bump`** — 2D AABB swept collision world (for top-down minigames, tactical grids, physics puzzles):
  - `local w = lib.bump.newWorld()`
  - `w:add(item, x, y, width, height)`, `w:remove(item)`, `w:update(item, x, y, width, height)`, `w:getRect(item)`, `w:hasItem(item)`
  - `local hits, n = w:queryRect(x, y, width, height, filter)`
  - `local ax, ay, cols, len = w:move(item, goalX, goalY, filter)` (`filter(item, other)` returns `"slide"`, `"touch"`, `"cross"`, or `nil`/`false`)

---

## 3. Sandbox Guarantees (Euler Claims C1–C9)

MeitouMoonscript is verified by an automated test and fuzz suite on every build:

- **No filesystem, network, process, or reflection access**: `io`, `os`, `package`, `require`, `dofile`, `loadfile`, `load`, `loadstring`, `debug`, and CLR interop are completely removed.
- **Hard instruction budgets**: infinite loops fault cleanly with `"budget"` and never hang the game.
- **Memory & string caps**: runaway table allocations or `string.rep` bombs fault cleanly with `"memory"`.
- **Deterministic RNG**: `math.random` is seeded per box via a fixed SplitMix64 generator (`os.time` / wall-clock entropy is never exposed).
- **Plain-data persistence**: `state` tables round-trip through `Save()` / `Restore()` with exact RNG state preserved.

---

## 4. C# Mod Integration (`IScriptHost` API v1)

Add `pza.moonscript` to your `meitou.json` dependencies:

```json
"dependencies": [
  { "id": "pza.moonscript", "version": ">=0.3.0" }
]
```

In your `IMod.Initialize(IModContext context)`:

```csharp
var host = context.Dependency<PZA.Moonscript.IScriptHost>("pza.moonscript");
var script = host.Open(new ScriptSpec
{
    Owner = "your.mod",
    Name = "npc-brain",
    Folder = Path.Combine(context.ModDirectory, "scripts"),
    File = "brain.lua",
    Seed = 12345,
    Functions = new[]
    {
        new HostFunction("ai", "distance", args => ScriptValue.Of(42.0))
    }
});

script.BeginTick(tick);
var decision = script.Call("decide", ScriptValue.Of(hp));
```

See [mod/packs/wasteland-dice/main.lua](../mod/packs/wasteland-dice/main.lua) and [mod/packs/combat-ticker/main.lua](../mod/packs/combat-ticker/main.lua) for complete working pack examples.
