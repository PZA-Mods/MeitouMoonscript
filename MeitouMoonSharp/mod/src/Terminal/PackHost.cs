// MeitouMoonscript: MTA:SA-inspired Lua Pack host (cross-mod discovery, isolated LuaBox per pack, global per-tick budget,
// collision-safe slash commands, deterministic game-clock timers, and narrow allow-listed game events).
// Author: PZA. Licence: GPLv3 or later.
//
// Security guards:
//   - HostedScript.SafePath blocks `..`, rooted paths, drive letters, and symlinks/junctions (ReparsePoint).
//   - Built-in packs live in <this mod>/packs/<id>/pack.json; third-party packs live in <modsRoot>/<otherMod>/moonscript/<id>/pack.json
//     and stay stopped by default unless Settings > Auto-start third-party packs is on (or the user runs `/start <id>`).
//   - Global per-tick instruction budget (GlobalTickBudget) caps total work across ALL packs combined each tick.
//   - Reserved host commands cannot be shadowed; if two packs claim the same command name, the first keeps `/<cmd>` and later packs
//     remain callable via `/<packid>:<cmd>` (never silently hijacking another pack).
//   - Narrow allow-listed game events only: "day", "hour", "hit", "character_down" (no root wildcard subscriptions).
using System.Text.Json;
using MoonSharp.Interpreter;
using PZA.Moonscript.Core;

namespace PZA.Moonscript.Repl;

public sealed record PackManifest(string Id, string Name, string Author, string Version, string Side, string Entry, string Description,
                                  string Source = "built-in", bool ThirdParty = false, IReadOnlyList<string>? Permissions = null)
{
    public IReadOnlyList<string> EffectivePermissions => Permissions ?? PackHost.DefaultPermissions;
}

public sealed record GameEvent(string Name, IReadOnlyDictionary<string, object?> Payload);

public sealed class PackHost
{
    public const long GlobalTickBudget = 800_000;
    public const int MaxActionsPerTick = 16;
    public const int MaxPackSaveBytes = 256 * 1024;

    public static readonly IReadOnlyList<string> DefaultPermissions = ["commands", "timers", "world.read"];

    public static readonly HashSet<string> SafePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "commands", "timers", "events", "world.read", "world.scan", "world.control", "world.items", "pack.save", "camera.read", "ui.hud",
    };

    readonly Dictionary<string, (string Owner, IReadOnlyList<HostFunction> Functions)> _extensions = new(StringComparer.OrdinalIgnoreCase);

    bool IsKnownPermission(string cap) => SafePermissions.Contains(cap) || _extensions.ContainsKey(cap);

    /// <summary>Register a C# extension capability (e.g. from another Meitou mod). Packs must declare it in pack.json and be approved via /allow.</summary>
    public bool RegisterExtension(string owner, string capability, IReadOnlyList<HostFunction> functions)
    {
        capability = (capability ?? "").Trim().ToLowerInvariant();
        if (capability.Length == 0 || capability.Length > 48 || SafePermissions.Contains(capability)) return false;
        if (capability.Any(c => !(char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))) return false;
        if (functions is null || functions.Count == 0) return false;
        _extensions[capability] = (owner ?? "ext", [.. functions]);
        _log($"pack extension registered: '{capability}' by {owner} ({functions.Count} function(s))");
        return true;
    }

    static readonly HashSet<string> ReservedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "run", "reload", "scripts", "config", "reset", "clear", "battle", "crawlers", "packs", "start", "stop", "restart", "allow", "revoke", "cancel", "kl",
    };

    public static readonly HashSet<string> AllowedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "day", "hour", "hit", "character_down",
    };

    readonly string _packsDir;
    readonly string? _modsRoot;
    readonly string? _selfModFolderName;
    readonly string? _grantsPath;
    readonly string? _stateDir;
    readonly Func<double?> _hours;
    readonly Func<string?> _selected;
    readonly Func<IReadOnlyList<FighterSnap>> _squad;
    readonly Func<long?> _cats;
    readonly Func<WorldScanSnapshot>? _scan;
    readonly Action<WorldAction>? _enqueueAction;
    readonly Func<CameraSnap?>? _camera;
    readonly HudState? _hud;
    readonly Func<ScriptLimits> _limits;
    readonly Action<string, Ink> _out;
    readonly Action<string> _log;

    readonly Dictionary<string, PackState> _packs = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, (PackState Pack, DynValue Fn)> _commands = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, HashSet<string>> _grants = new(StringComparer.OrdinalIgnoreCase);
    ulong _seed = 1001;
    long _nextTimerId;
    double _lastHour = double.NaN;
    public long LastTickInstructions { get; private set; }
    public int GlobalBudgetSkips { get; private set; }

    sealed class TimerEntry
    {
        public long Id;
        public double DueHour;
        public double Interval;
        public DynValue Fn = DynValue.Nil;
    }

    sealed class PackState(PackManifest manifest, string folder)
    {
        public PackManifest Manifest { get; } = manifest;
        public string Folder { get; } = folder;
        public LuaBox? Box;
        public bool Running;
        public string? Error;
        public int ActionsThisTick;
        public bool CanSave;
        public bool SaveRequested;
        public List<string> Commands { get; } = [];
        public List<TimerEntry> Timers { get; } = [];
        public Dictionary<string, List<DynValue>> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> HudCallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public PackHost(string packsDir, Func<double?> hours, Func<string?> selected, Func<IReadOnlyList<FighterSnap>> squad,
                    Func<long?> cats, Func<ScriptLimits> limits, Action<string, Ink> output, Action<string> log,
                    string? modsRoot = null, string? selfModFolderName = null, string? grantsPath = null,
                    Func<WorldScanSnapshot>? scan = null, Action<WorldAction>? enqueueAction = null, string? stateDir = null,
                    Func<CameraSnap?>? camera = null, HudState? hud = null)
    {
        _packsDir = packsDir; _modsRoot = modsRoot; _selfModFolderName = selfModFolderName; _grantsPath = grantsPath;
        _hours = hours; _selected = selected; _squad = squad; _cats = cats;
        _scan = scan; _enqueueAction = enqueueAction; _stateDir = stateDir; _camera = camera; _hud = hud;
        _limits = limits; _out = output; _log = log;
        LoadGrants();
        Discover();
    }

    void LoadGrants()
    {
        if (string.IsNullOrEmpty(_grantsPath) || !File.Exists(_grantsPath)) return;
        try
        {
            var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_grantsPath));
            if (data is null) return;
            foreach (var (id, caps) in data)
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in caps ?? []) if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim().ToLowerInvariant());
                if (set.Count > 0) _grants[id] = set;
            }
        }
        catch (Exception e) { _log($"packs.grants.json could not be read: {e.Message}"); }
    }

    void SaveGrants()
    {
        if (string.IsNullOrEmpty(_grantsPath)) return;
        try
        {
            var data = _grants.Where(kv => kv.Value.Count > 0)
                .ToDictionary(kv => kv.Key, kv => kv.Value.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(), StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(_grantsPath, JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { _log($"packs.grants.json could not be saved: {e.Message}"); }
    }

    public IReadOnlyList<string> MissingPermissions(PackManifest m)
    {
        if (!m.ThirdParty) return [];
        _grants.TryGetValue(m.Id, out var granted);
        return m.EffectivePermissions.Where(p => granted is null || !granted.Contains(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public (bool ok, string message) Allow(string id, string cap = "all")
    {
        Discover();
        if (!_packs.TryGetValue(id, out var p)) return (false, $"unknown pack '{id}' (see /packs)");
        var req = p.Manifest.EffectivePermissions;
        var unsupported = req.Where(c => !IsKnownPermission(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unsupported.Count > 0)
            return (false, $"pack '{id}' requests unsupported permission(s): {string.Join(", ", unsupported)}");
        cap = (cap ?? "all").Trim().ToLowerInvariant();
        if (cap.Length == 0) cap = "all";
        if (!_grants.TryGetValue(id, out var set)) _grants[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (cap == "all")
        {
            foreach (var c in req) set.Add(c.ToLowerInvariant());
            SaveGrants();
            return (true, $"allowed '{id}': {string.Join(", ", set.OrderBy(x => x))}");
        }
        if (!IsKnownPermission(cap))
            return (false, $"unknown or unsupported permission '{cap}' (known: {string.Join(", ", SafePermissions.Concat(_extensions.Keys).OrderBy(x => x))})");
        set.Add(cap);
        SaveGrants();
        return (true, $"allowed '{cap}' for pack '{id}'");
    }

    public (bool ok, string message) Revoke(string id, string cap = "all")
    {
        if (!_packs.TryGetValue(id, out var p)) return (false, $"unknown pack '{id}'");
        cap = (cap ?? "all").Trim().ToLowerInvariant();
        if (cap.Length == 0) cap = "all";
        if (cap == "all") _grants.Remove(id);
        else if (_grants.TryGetValue(id, out var set)) { set.Remove(cap); if (set.Count == 0) _grants.Remove(id); }
        SaveGrants();
        if (p.Running) Stop(id);
        return (true, cap == "all" ? $"revoked all permissions for '{id}'" : $"revoked '{cap}' for '{id}'");
    }

    public int Discover()
    {
        int found = ScanRoot(_packsDir, "built-in", thirdParty: false);
        if (!string.IsNullOrEmpty(_modsRoot) && Directory.Exists(_modsRoot))
        {
            foreach (var modDir in Directory.GetDirectories(_modsRoot).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                string modName = Path.GetFileName(modDir);
                if (!string.IsNullOrEmpty(_selfModFolderName) && modName.Equals(_selfModFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;
                try
                {
                    if ((File.GetAttributes(modDir) & FileAttributes.ReparsePoint) != 0) continue;
                    string msDir = Path.Combine(modDir, "moonscript");
                    if (Directory.Exists(msDir) && (File.GetAttributes(msDir) & FileAttributes.ReparsePoint) == 0)
                        found += ScanRoot(msDir, "mod:" + modName, thirdParty: true);
                }
                catch (Exception) { }
            }
        }
        return found;
    }

    int ScanRoot(string rootDir, string source, bool thirdParty)
    {
        if (!Directory.Exists(rootDir)) return 0;
        int found = 0;
        foreach (var dir in Directory.GetDirectories(rootDir).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue;
                string metaPath = Path.Combine(dir, "pack.json");
                if (!File.Exists(metaPath) || (File.GetAttributes(metaPath) & FileAttributes.ReparsePoint) != 0) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                string id = root.TryGetProperty("id", out var pId) ? (pId.GetString() ?? "").Trim() : Path.GetFileName(dir);
                if (id.Length == 0 || id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))) continue;
                string name = root.TryGetProperty("name", out var pN) ? pN.GetString() ?? id : id;
                string author = root.TryGetProperty("author", out var pA) ? pA.GetString() ?? "unknown" : "unknown";
                string ver = root.TryGetProperty("version", out var pV) ? pV.GetString() ?? "0.1.0" : "0.1.0";
                string side = root.TryGetProperty("side", out var pS) ? pS.GetString() ?? "ui" : "ui";
                string entry = root.TryGetProperty("entry", out var pE) ? pE.GetString() ?? "main.lua" : "main.lua";
                string desc = root.TryGetProperty("description", out var pD) ? pD.GetString() ?? "" : "";
                List<string>? perms = null;
                if (root.TryGetProperty("permissions", out var pP) && pP.ValueKind == JsonValueKind.Array)
                {
                    perms = [];
                    foreach (var el in pP.EnumerateArray())
                    {
                        var s = (el.GetString() ?? "").Trim().ToLowerInvariant();
                        if (s.Length > 0 && !perms.Contains(s, StringComparer.OrdinalIgnoreCase)) perms.Add(s);
                    }
                }
                var m = new PackManifest(id, name, author, ver, side, entry, desc, source, thirdParty, perms);
                if (!_packs.ContainsKey(id)) _packs[id] = new PackState(m, dir);
                found++;
            }
            catch (Exception e) { _log($"pack.json skipped in {Path.GetFileName(dir)}: {e.Message}"); }
        }
        return found;
    }

    public IReadOnlyList<(PackManifest Manifest, bool Running, string? Error, IReadOnlyList<string> Commands)> List() =>
        _packs.Values.OrderBy(p => p.Manifest.Id, StringComparer.OrdinalIgnoreCase)
            .Select(p => (p.Manifest, p.Running, p.Error, (IReadOnlyList<string>)p.Commands)).ToList();

    public IReadOnlyList<(string Name, string PackId)> RegisteredCommands() =>
        _commands.Where(kv => !kv.Key.Contains(':')).OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => (kv.Key, kv.Value.Pack.Manifest.Id)).ToList();

    public (bool ok, string message) Start(string id)
    {
        Discover();
        if (!_packs.TryGetValue(id, out var p)) return (false, $"unknown pack '{id}' (see /packs)");
        if (p.Running) return (true, $"pack '{id}' is already running");

        var reqPerms = p.Manifest.EffectivePermissions;
        var unsupported = reqPerms.Where(c => !IsKnownPermission(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (unsupported.Count > 0)
            return (false, $"pack '{id}' requests unsupported permission(s): {string.Join(", ", unsupported)}");
        var missing = MissingPermissions(p.Manifest);
        if (missing.Count > 0)
            return (false, $"pack '{id}' needs permission approval ({string.Join(", ", missing)}): run /allow {id} all");

        var granted = new HashSet<string>(reqPerms, StringComparer.OrdinalIgnoreCase);

        var entryPath = HostedScript.SafePath(p.Folder, p.Manifest.Entry);
        if (entryPath is null || !File.Exists(entryPath)) return (false, $"pack '{id}': entry '{p.Manifest.Entry}' not found or refused");
        string text;
        try { text = File.ReadAllText(entryPath); }
        catch (Exception e) { return (false, $"pack '{id}': {e.GetType().Name}"); }

        var box = new LuaBox("pack:" + id, _limits(), _seed++, s => _out(s, Ink.Text), _log) { Forgiving = true };
        if (granted.Contains("world.read"))
        {
            box.Expose("world", "hour", _ => _hours() is double h ? DynValue.NewNumber(Math.Floor(((h % 24) + 24) % 24 * 100) / 100) : DynValue.Nil);
            box.Expose("world", "day", _ => _hours() is double h ? DynValue.NewNumber(Math.Max(1, Math.Floor(h / 24))) : DynValue.Nil);
            box.Expose("world", "cats", _ => _cats() is long c ? DynValue.NewNumber(c) : DynValue.Nil);
            box.Expose("world", "squad", _ => ReplSession.ToLuaSquad(box.RawScript, _squad()));
            box.Expose("selected", "name", _ => _selected() is { Length: > 0 } n ? DynValue.NewString(n) : DynValue.Nil);
        }
        if (granted.Contains("commands")) box.Expose("", "command", a => RegisterCommand(p, a));
        if (granted.Contains("timers"))
        {
            box.Expose("", "after", a => RegisterTimer(p, a, repeat: false));
            box.Expose("", "every", a => RegisterTimer(p, a, repeat: true));
        }
        if (granted.Contains("events")) box.Expose("", "on", a => RegisterEvent(p, a));
        if (granted.Contains("world.scan"))
        {
            box.Expose("world", "characters", a => ScanCharacters(box.RawScript, a));
            box.Expose("world", "doors", a => ScanDoors(box.RawScript, a));
            box.Expose("world", "buildings", a => ScanBuildings(box.RawScript, a));
        }
        if (granted.Contains("world.control"))
        {
            box.Expose("doors", "toggle", a => QueueDoor(p, "door.toggle", a, hasFlag: false));
            box.Expose("doors", "set_open", a => QueueDoor(p, "door.set_open", a, hasFlag: true));
            box.Expose("doors", "toggle_lock", a => QueueDoor(p, "door.toggle_lock", a, hasFlag: false));
            box.Expose("doors", "set_locked", a => QueueDoor(p, "door.set_locked", a, hasFlag: true));
            box.Expose("orders", "move", a => QueueMove(p, a));
            box.Expose("orders", "stop", a => QueueStop(p, a));
            box.Expose("orders", "follow", a => QueueFollow(p, a));
            box.Expose("orders", "attack", a => QueueAttack(p, a));
            box.Expose("orders", "squad_ai", a => QueueSquadAi(p, a));
            box.Expose("world", "floater", a => QueueFloater(p, a));
        }
        p.CanSave = granted.Contains("pack.save");
        if (p.CanSave) box.Expose("pack", "save", _ => { p.SaveRequested = true; return DynValue.True; });
        if (granted.Contains("camera.read"))
        {
            box.Expose("camera", "pos", _ => CameraPos(box.RawScript));
            box.Expose("camera", "forward", _ => CameraForward(box.RawScript));
            box.Expose("camera", "yaw", _ => _camera?.Invoke() is { } c ? DynValue.NewNumber(c.Yaw) : DynValue.Nil);
            box.Expose("camera", "pitch", _ => _camera?.Invoke() is { } c ? DynValue.NewNumber(c.Pitch) : DynValue.Nil);
            box.Expose("camera", "fov", _ => _camera?.Invoke() is { } c ? DynValue.NewNumber(c.FovY) : DynValue.Nil);
            box.Expose("camera", "project", a => CameraProject(box.RawScript, a));
        }
        if (granted.Contains("world.items"))
        {
            box.Expose("items", "list", a => ListItems(box.RawScript, a));
            box.Expose("items", "give", a => QueueItemGive(p, a));
            box.Expose("items", "remove", a => QueueItemRemove(p, a));
            box.Expose("world", "add_cats", a => QueueAddCats(p, a));
        }
        if (granted.Contains("ui.hud"))
        {
            box.Expose("hud", "set_text", a => HudSetText(p, a));
            box.Expose("hud", "set_bar", a => HudSetBar(p, a));
            box.Expose("hud", "set_button", a => HudSetButton(p, a));
            box.Expose("hud", "remove", a => HudRemove(p, a));
            box.Expose("hud", "clear", _ => { p.HudCallbacks.Clear(); _hud?.ClearPack(p.Manifest.Id); return DynValue.True; });
        }
        foreach (var cap in granted)
        {
            if (!_extensions.TryGetValue(cap, out var ext)) continue;
            foreach (var fn in ext.Functions)
            {
                var captured = fn;
                box.Expose(captured.Table, captured.Name, a =>
                {
                    var args = new ScriptValue[a.Count];
                    for (int i = 0; i < a.Count; i++) args[i] = ScriptValue.FromLua(a[i]);
                    return captured.Call(args).ToLua(box.RawScript);
                });
            }
        }

        p.Box = box;
        p.Error = null;
        p.ActionsThisTick = 0;
        p.Commands.Clear();
        p.Timers.Clear();
        p.Events.Clear();
        p.HudCallbacks.Clear();
        box.Load(p.Manifest.Entry, text);
        if (box.Faulted || box.LastError is not null)
        {
            p.Error = box.Faulted ? $"{box.Fault}: {box.FaultDetail}" : box.LastError;
            Stop(id);
            return (false, $"pack '{id}' failed: {p.Error}");
        }
        if (p.CanSave) LoadPackState(p);
        p.Running = true;
        string cmds = p.Commands.Count > 0 ? " (commands: " + string.Join(", ", p.Commands.Select(c => "/" + c)) + ")" : "";
        return (true, $"started pack '{id}' v{p.Manifest.Version}{cmds}");
    }

    public (bool ok, string message) Stop(string id)
    {
        if (!_packs.TryGetValue(id, out var p)) return (false, $"unknown pack '{id}'");
        if (p.Running && p.CanSave && p.Box is { Faulted: false }) SavePackState(p);
        _hud?.ClearPack(p.Manifest.Id);
        var mine = _commands.Where(kv => ReferenceEquals(kv.Value.Pack, p)).Select(kv => kv.Key).ToList();
        foreach (var k in mine) _commands.Remove(k);
        p.Commands.Clear();
        p.Timers.Clear();
        p.Events.Clear();
        p.HudCallbacks.Clear();
        p.Box = null;
        p.Running = false;
        p.CanSave = false;
        return (true, $"stopped pack '{id}'");
    }

    public (bool ok, string message) Restart(string id)
    {
        if (!_packs.ContainsKey(id)) Discover();
        if (!_packs.ContainsKey(id)) return (false, $"unknown pack '{id}'");
        Stop(id);
        return Start(id);
    }

    public bool TryCommand(string name, string arg, out string? error, out long instructions)
    {
        error = null; instructions = 0;
        if (!_commands.TryGetValue(name, out var reg)) return false;
        var p = reg.Pack;
        if (!p.Running || p.Box is null) { error = $"pack '{p.Manifest.Id}' is not running"; return true; }
        p.ActionsThisTick = 0;
        var box = p.Box;
        box.RawScript.Globals["__cmd_fn"] = reg.Fn;
        box.RawScript.Globals["__cmd_arg"] = DynValue.NewString(arg);
        box.Eval("__cmd_fn(__cmd_arg)");
        instructions = box.LastCallInstructions;
        if (box.Faulted)
        {
            error = $"pack '{p.Manifest.Id}' faulted ({box.Fault}: {box.FaultDetail}) and was stopped";
            Stop(p.Manifest.Id);
        }
        else if (box.LastError is not null)
        {
            error = box.LastError;
        }
        else if (p.SaveRequested)
        {
            p.SaveRequested = false;
            SavePackState(p);
        }
        return true;
    }

    /// <summary>Advance deterministic game-clock timers and deliver queued game events under a single global per-tick budget.</summary>
    public void Tick(double? gameHours, IReadOnlyList<GameEvent>? events = null)
    {
        foreach (var ps in _packs.Values) ps.ActionsThisTick = 0;
        long used = 0;
        if (gameHours is double now && double.IsFinite(now))
        {
            if (!double.IsNaN(_lastHour) && now < _lastHour) _lastHour = now;
            _lastHour = now;

            foreach (var p in _packs.Values.OrderBy(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase))
            {
                if (!p.Running || p.Box is null || p.Timers.Count == 0) continue;
                var due = p.Timers.Where(t => t.DueHour <= now).OrderBy(t => t.DueHour).ThenBy(t => t.Id).ToList();
                foreach (var t in due)
                {
                    if (!p.Running || p.Box is null) break;
                    if (used >= GlobalTickBudget) { GlobalBudgetSkips++; break; }
                    if (t.Interval > 0) t.DueHour = now + t.Interval;
                    else p.Timers.Remove(t);

                    p.Box.RawScript.Globals["__tmr_fn"] = t.Fn;
                    p.Box.Eval("__tmr_fn()");
                    used += p.Box.LastCallInstructions;
                    if (p.Box.Faulted)
                    {
                        _log($"pack '{p.Manifest.Id}' timer faulted: {p.Box.Fault}: {p.Box.FaultDetail}");
                        Stop(p.Manifest.Id);
                        break;
                    }
                }
            }
        }

        if (events is { Count: > 0 })
        {
            foreach (var ev in events)
            {
                if (used >= GlobalTickBudget) { GlobalBudgetSkips++; break; }
                used += DeliverEvent(ev, GlobalTickBudget - used);
            }
        }
        foreach (var p in _packs.Values)
            if (p.Running && p.CanSave && p.SaveRequested && p.Box is { Faulted: false })
            {
                p.SaveRequested = false;
                SavePackState(p);
            }
        LastTickInstructions = used;
    }

    /// <summary>Deliver one narrow game event to every running pack subscribed to it (under the global per-tick budget).</summary>
    public long Emit(string name, IReadOnlyDictionary<string, object?> payload) =>
        DeliverEvent(new GameEvent(name, payload), GlobalTickBudget);

    long DeliverEvent(GameEvent ev, long budgetLeft)
    {
        if (!AllowedEvents.Contains(ev.Name)) return 0;
        long used = 0;
        foreach (var p in _packs.Values.OrderBy(x => x.Manifest.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (!p.Running || p.Box is null || !p.Events.TryGetValue(ev.Name, out var list) || list.Count == 0) continue;
            foreach (var fn in list.ToList())
            {
                if (!p.Running || p.Box is null) break;
                if (used >= budgetLeft) { GlobalBudgetSkips++; return used; }
                var tbl = new Table(p.Box.RawScript);
                foreach (var kv in ev.Payload)
                    tbl[kv.Key] = kv.Value switch
                    {
                        null => DynValue.Nil,
                        bool b => DynValue.NewBoolean(b),
                        int i => DynValue.NewNumber(i),
                        long l => DynValue.NewNumber(l),
                        double d => DynValue.NewNumber(d),
                        float f => DynValue.NewNumber(f),
                        string s => DynValue.NewString(s),
                        _ => DynValue.NewString(kv.Value.ToString() ?? ""),
                    };
                p.Box.RawScript.Globals["__ev_fn"] = fn;
                p.Box.RawScript.Globals["__ev_arg"] = DynValue.NewTable(tbl);
                p.Box.Eval("__ev_fn(__ev_arg)");
                used += p.Box.LastCallInstructions;
                if (p.Box.Faulted)
                {
                    _log($"pack '{p.Manifest.Id}' on('{ev.Name}') faulted: {p.Box.Fault}: {p.Box.FaultDetail}");
                    Stop(p.Manifest.Id);
                    break;
                }
            }
        }
        return used;
    }

    DynValue RegisterCommand(PackState p, CallbackArguments a)
    {
        if (a.Count < 2 || a[0].Type != DataType.String || a[1].Type != DataType.Function)
            throw new ScriptRuntimeException("command(name, fn) expects a string name and a function");
        string name = a[0].String.Trim().TrimStart('/').ToLowerInvariant();
        if (name.Length == 0 || name.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new ScriptRuntimeException($"command: invalid name '{name}'");
        if (ReservedCommands.Contains(name))
            throw new ScriptRuntimeException($"command: '/{name}' is reserved by the host");

        string ns = p.Manifest.Id.ToLowerInvariant() + ":" + name;
        _commands[ns] = (p, a[1]);
        if (!_commands.TryGetValue(name, out var existing) || ReferenceEquals(existing.Pack, p))
        {
            _commands[name] = (p, a[1]);
            if (!p.Commands.Contains(name)) p.Commands.Add(name);
        }
        else
        {
            // First-come wins bare `/<name>` so a later pack never silently hijacks an earlier pack's command
            if (!p.Commands.Contains(ns)) p.Commands.Add(ns);
            _log($"pack '{p.Manifest.Id}': '/{name}' is already owned by '{existing.Pack.Manifest.Id}'; registered as '/{ns}'");
        }
        return DynValue.True;
    }

    DynValue RegisterTimer(PackState p, CallbackArguments a, bool repeat)
    {
        if (a.Count < 2 || a[0].Type != DataType.Number || a[1].Type != DataType.Function)
            throw new ScriptRuntimeException($"{(repeat ? "every" : "after")}(hours, fn) expects a number of game hours and a function");
        double h = a[0].Number;
        if (!double.IsFinite(h) || h < 0) throw new ScriptRuntimeException("timer hours must be >= 0");
        if (repeat) h = Math.Max(0.01, h);
        double now = _hours() ?? (double.IsNaN(_lastHour) ? 0 : _lastHour);
        long id = ++_nextTimerId;
        p.Timers.Add(new TimerEntry { Id = id, DueHour = now + h, Interval = repeat ? h : 0, Fn = a[1] });
        return DynValue.NewNumber(id);
    }

    DynValue RegisterEvent(PackState p, CallbackArguments a)
    {
        if (a.Count < 2 || a[0].Type != DataType.String || a[1].Type != DataType.Function)
            throw new ScriptRuntimeException("on(event, fn) expects an event name and a function");
        string name = a[0].String.Trim().ToLowerInvariant();
        if (!AllowedEvents.Contains(name))
            throw new ScriptRuntimeException($"on: unknown event '{name}' (allowed: {string.Join(", ", AllowedEvents.OrderBy(x => x))})");
        if (!p.Events.TryGetValue(name, out var list)) p.Events[name] = list = [];
        list.Add(a[1]);
        return DynValue.True;
    }

    // ---------------------------------------------------------------- world.scan (0.4.0 sensors)

    DynValue ScanCharacters(Script s, CallbackArguments a)
    {
        var snap = _scan?.Invoke() ?? WorldScanSnapshot.Empty;
        bool hasCenter = a.Count >= 3 && a[0].Type == DataType.Number && a[1].Type == DataType.Number && a[2].Type == DataType.Number;
        double cx = hasCenter ? a[0].Number : 0, cy = hasCenter ? a[1].Number : 0, r = hasCenter ? Math.Max(0, a[2].Number) : 0;
        double r2 = r * r;
        string filter = "";
        if (hasCenter && a.Count >= 4 && a[3].Type == DataType.String) filter = a[3].String.Trim().ToLowerInvariant();
        else if (!hasCenter && a.Count >= 1 && a[0].Type == DataType.String) filter = a[0].String.Trim().ToLowerInvariant();

        var arr = new Table(s);
        int idx = 0;
        foreach (var c in snap.Characters)
        {
            if (filter == "player" && !c.IsPlayer) continue;
            if (filter == "npc" && c.IsPlayer) continue;
            if (filter == "alive" && c.Dead) continue;
            if (filter == "combat" && !c.InCombat) continue;
            double dist = 0;
            if (hasCenter)
            {
                double dx = c.X - cx, dz = c.Z - cy;
                double d2 = dx * dx + dz * dz;
                if (d2 > r2) continue;
                dist = Math.Sqrt(d2);
            }
            var t = new Table(s);
            t["slot"] = c.Slot; t["gen"] = c.Gen; t["name"] = c.Name;
            t["faction"] = c.Faction; t["is_player"] = c.IsPlayer;
            t["x"] = c.X; t["y"] = c.Y; t["z"] = c.Z; t["dist"] = dist;
            t["hp"] = c.Hp; t["down"] = c.Down; t["dead"] = c.Dead;
            t["in_combat"] = c.InCombat; t["squad_id"] = c.SquadId;
            arr[++idx] = t;
        }
        return DynValue.NewTable(arr);
    }

    DynValue ScanDoors(Script s, CallbackArguments a)
    {
        var snap = _scan?.Invoke() ?? WorldScanSnapshot.Empty;
        bool onlyPlayer = a.Count >= 1 && a[0].Type == DataType.Boolean && a[0].Boolean;
        var arr = new Table(s);
        int idx = 0;
        foreach (var d in snap.Doors)
        {
            if (onlyPlayer && !d.IsPlayer) continue;
            var t = new Table(s);
            t["id"] = d.Id; t["building"] = d.Building; t["owner"] = d.Owner; t["is_player"] = d.IsPlayer;
            t["x"] = d.X; t["y"] = d.Y; t["z"] = d.Z;
            t["open_amount"] = d.OpenAmount; t["motion"] = d.Motion;
            t["locked"] = d.Locked; t["broken"] = d.Broken; t["is_gate"] = d.IsGate;
            arr[++idx] = t;
        }
        return DynValue.NewTable(arr);
    }

    DynValue ScanBuildings(Script s, CallbackArguments a)
    {
        var snap = _scan?.Invoke() ?? WorldScanSnapshot.Empty;
        bool onlyPlayer = a.Count >= 1 && a[0].Type == DataType.Boolean && a[0].Boolean;
        var arr = new Table(s);
        int idx = 0;
        foreach (var b in snap.Buildings)
        {
            if (onlyPlayer && !b.IsPlayer) continue;
            var t = new Table(s);
            t["id"] = b.Id; t["name"] = b.Name; t["record"] = b.Record;
            t["owner"] = b.Owner; t["is_player"] = b.IsPlayer;
            t["x"] = b.X; t["y"] = b.Y; t["z"] = b.Z;
            t["complete"] = b.Complete; t["progress"] = b.Progress;
            arr[++idx] = t;
        }
        return DynValue.NewTable(arr);
    }

    // ---------------------------------------------------------------- world.control (0.4.0 rate-limited actuators)

    bool TrySpendAction(PackState p)
    {
        if (_enqueueAction is null) return false;
        if (p.ActionsThisTick >= MaxActionsPerTick) return false;
        p.ActionsThisTick++;
        return true;
    }

    DynValue QueueDoor(PackState p, string kind, CallbackArguments a, bool hasFlag)
    {
        if (a.Count < (hasFlag ? 2 : 1) || a[0].Type != DataType.String)
            throw new ScriptRuntimeException($"{kind}: expected building id string" + (hasFlag ? " and boolean" : ""));
        string bld = a[0].String.Trim();
        if (bld.Length == 0 || bld.Length > 128) throw new ScriptRuntimeException($"{kind}: invalid building id");
        bool flag = hasFlag && a[1].CastToBool();
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction(kind, TargetId: bld, Flag: flag));
        return DynValue.True;
    }

    static List<(int Slot, int Gen)> ParseCharIds(DynValue v, string who)
    {
        var list = new List<(int, int)>();
        if (v.Type != DataType.Table) throw new ScriptRuntimeException($"{who}: expected a character table {{slot, gen}} or array of them");
        var t = v.Table;
        if (t.Get("slot").Type == DataType.Number && t.Get("gen").Type == DataType.Number)
        {
            list.Add(((int)t.Get("slot").Number, (int)t.Get("gen").Number));
            return list;
        }
        int len = Math.Min(t.Length, 32);
        for (int i = 1; i <= len; i++)
        {
            var item = t.Get(i);
            if (item.Type == DataType.Table && item.Table.Get("slot").Type == DataType.Number && item.Table.Get("gen").Type == DataType.Number)
                list.Add(((int)item.Table.Get("slot").Number, (int)item.Table.Get("gen").Number));
        }
        if (list.Count == 0) throw new ScriptRuntimeException($"{who}: no valid {{slot, gen}} character ids found");
        return list;
    }

    static (int Slot, int Gen) ParseSingleChar(DynValue v, string who)
    {
        if (v.Type != DataType.Table || v.Table.Get("slot").Type != DataType.Number || v.Table.Get("gen").Type != DataType.Number)
            throw new ScriptRuntimeException($"{who}: expected target character table {{slot, gen}}");
        return ((int)v.Table.Get("slot").Number, (int)v.Table.Get("gen").Number);
    }

    static double FiniteNum(DynValue v, string who)
    {
        if (v.Type != DataType.Number || !double.IsFinite(v.Number))
            throw new ScriptRuntimeException($"{who}: expected finite number");
        return v.Number;
    }

    DynValue QueueMove(PackState p, CallbackArguments a)
    {
        if (a.Count < 4) throw new ScriptRuntimeException("orders.move(chars, x, y, z) expects 4 arguments");
        var ids = ParseCharIds(a[0], "orders.move");
        double x = FiniteNum(a[1], "orders.move"), y = FiniteNum(a[2], "orders.move"), z = FiniteNum(a[3], "orders.move");
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("order.move", CharIds: ids, X: x, Y: y, Z: z));
        return DynValue.True;
    }

    DynValue QueueStop(PackState p, CallbackArguments a)
    {
        if (a.Count < 1) throw new ScriptRuntimeException("orders.stop(chars) expects character id(s)");
        var ids = ParseCharIds(a[0], "orders.stop");
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("order.stop", CharIds: ids));
        return DynValue.True;
    }

    DynValue QueueFollow(PackState p, CallbackArguments a)
    {
        if (a.Count < 2) throw new ScriptRuntimeException("orders.follow(chars, target[, guard]) expects chars and target");
        var ids = ParseCharIds(a[0], "orders.follow");
        var (ts, tg) = ParseSingleChar(a[1], "orders.follow");
        bool guard = a.Count >= 3 && a[2].CastToBool();
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("order.follow", CharIds: ids, TargetSlot: ts, TargetGen: tg, Flag: guard));
        return DynValue.True;
    }

    DynValue QueueAttack(PackState p, CallbackArguments a)
    {
        if (a.Count < 2) throw new ScriptRuntimeException("orders.attack(chars, target) expects chars and target");
        var ids = ParseCharIds(a[0], "orders.attack");
        var (ts, tg) = ParseSingleChar(a[1], "orders.attack");
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("order.attack", CharIds: ids, TargetSlot: ts, TargetGen: tg));
        return DynValue.True;
    }

    DynValue QueueSquadAi(PackState p, CallbackArguments a)
    {
        if (a.Count < 2 || a[0].Type != DataType.String)
            throw new ScriptRuntimeException("orders.squad_ai(option, on) expects option name and boolean");
        string opt = a[0].String.Trim().ToLowerInvariant();
        if (opt.Length == 0 || opt.Length > 32) throw new ScriptRuntimeException("orders.squad_ai: invalid option");
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("order.squad_ai", Text: opt, Flag: a[1].CastToBool()));
        return DynValue.True;
    }

    DynValue QueueFloater(PackState p, CallbackArguments a)
    {
        if (a.Count < 4 || a[3].Type != DataType.String)
            throw new ScriptRuntimeException("world.floater(x, y, z, text) expects coordinates and text");
        double x = FiniteNum(a[0], "world.floater"), y = FiniteNum(a[1], "world.floater"), z = FiniteNum(a[2], "world.floater");
        string text = a[3].String.Trim();
        if (text.Length == 0) return DynValue.False;
        if (text.Length > 80) text = text[..80];
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("world.floater", X: x, Y: y, Z: z, Text: text));
        return DynValue.True;
    }

    // ---------------------------------------------------------------- pack.save (0.4.0 C7-safe persistence)

    string? StateFilePath(string id)
    {
        if (string.IsNullOrEmpty(_stateDir)) return null;
        return HostedScript.SafePath(_stateDir, id.ToLowerInvariant() + ".bin");
    }

    bool SavePackState(PackState p)
    {
        if (p.Box is null || p.Box.Faulted) return false;
        string? path = StateFilePath(p.Manifest.Id);
        if (path is null) return false;
        try
        {
            byte[] data = p.Box.Save();
            if (data.Length > MaxPackSaveBytes)
            {
                _log($"pack '{p.Manifest.Id}': state too large to save ({data.Length} > {MaxPackSaveBytes} bytes)");
                return false;
            }
            Directory.CreateDirectory(_stateDir!);
            File.WriteAllBytes(path, data);
            return true;
        }
        catch (Exception e) { _log($"pack '{p.Manifest.Id}': save failed: {e.Message}"); return false; }
    }

    void LoadPackState(PackState p)
    {
        if (p.Box is null || p.Box.Faulted) return;
        string? path = StateFilePath(p.Manifest.Id);
        if (path is null || !File.Exists(path)) return;
        try
        {
            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > MaxPackSaveBytes) return;
            p.Box.Restore(File.ReadAllBytes(path));
        }
        catch (Exception e) { _log($"pack '{p.Manifest.Id}': saved state ignored: {e.Message}"); }
    }

    // ---------------------------------------------------------------- camera.read & ui.hud (0.5.0)

    DynValue CameraPos(Script s)
    {
        if (_camera?.Invoke() is not { } c) return DynValue.Nil;
        var t = new Table(s);
        t["x"] = c.X; t["y"] = c.Y; t["z"] = c.Z;
        return DynValue.NewTable(t);
    }

    DynValue CameraForward(Script s)
    {
        if (_camera?.Invoke() is not { } c) return DynValue.Nil;
        var t = new Table(s);
        t["x"] = c.FwdX; t["y"] = c.FwdY; t["z"] = c.FwdZ;
        return DynValue.NewTable(t);
    }

    DynValue CameraProject(Script s, CallbackArguments a)
    {
        if (_camera?.Invoke() is not { } c) return DynValue.Nil;
        if (a.Count < 3) throw new ScriptRuntimeException("camera.project(x, y, z) expects 3 numbers");
        double wx = FiniteNum(a[0], "camera.project"), wy = FiniteNum(a[1], "camera.project"), wz = FiniteNum(a[2], "camera.project");
        double dx = wx - c.X, dy = wy - c.Y, dz = wz - c.Z;
        double fx = c.FwdX, fy = c.FwdY, fz = c.FwdZ;
        // right = normalize(cross(fwd, worldUp(0,1,0))) = (-fz, 0, fx)
        double rx = -fz, ry = 0, rz = fx;
        double rlen = Math.Sqrt(rx * rx + rz * rz);
        if (rlen > 1e-6) { rx /= rlen; rz /= rlen; } else { rx = 1; rz = 0; }
        // up = cross(right, fwd)
        double ux = ry * fz - rz * fy;
        double uy = rz * fx - rx * fz;
        double uz = rx * fy - ry * fx;

        double depth = dx * fx + dy * fy + dz * fz;
        var t = new Table(s);
        if (depth <= 0.1)
        {
            t["x"] = -1; t["y"] = -1; t["visible"] = false; t["dist"] = depth;
            return DynValue.NewTable(t);
        }
        double cx = dx * rx + dy * ry + dz * rz;
        double cy = dx * ux + dy * uy + dz * uz;
        double fovRad = c.FovY > 3.5 ? c.FovY * Math.PI / 180.0 : c.FovY;
        if (!double.IsFinite(fovRad) || fovRad <= 0.05) fovRad = Math.PI / 3.0;
        double tanHalfY = Math.Tan(fovRad * 0.5);
        double aspect = double.IsFinite(c.Aspect) && c.Aspect > 0.1 ? c.Aspect : (16.0 / 9.0);
        double ndcX = cx / (depth * tanHalfY * aspect);
        double ndcY = cy / (depth * tanHalfY);
        double sx = 0.5 + 0.5 * ndcX;
        double sy = 0.5 - 0.5 * ndcY;
        t["x"] = sx; t["y"] = sy; t["dist"] = depth;
        t["visible"] = sx >= 0.0 && sx <= 1.0 && sy >= 0.0 && sy <= 1.0;
        return DynValue.NewTable(t);
    }

    DynValue HudSetText(PackState p, CallbackArguments a)
    {
        if (_hud is null) return DynValue.False;
        if (a.Count < 6 || a[0].Type != DataType.String || a[5].Type != DataType.String)
            throw new ScriptRuntimeException("hud.set_text(id, x, y, w, h, text[, color]) expects id, 4 numbers, and text");
        string id = a[0].String.Trim();
        double x = FiniteNum(a[1], "hud.set_text"), y = FiniteNum(a[2], "hud.set_text");
        double w = FiniteNum(a[3], "hud.set_text"), h = FiniteNum(a[4], "hud.set_text");
        string text = a[5].String;
        string color = a.Count >= 7 && a[6].Type == DataType.String ? a[6].String.Trim() : "#F0E6D2";
        return DynValue.NewBoolean(_hud.SetText(p.Manifest.Id, id, x, y, w, h, text, color));
    }

    DynValue HudRemove(PackState p, CallbackArguments a)
    {
        if (_hud is null || a.Count < 1 || a[0].Type != DataType.String) return DynValue.False;
        string id = a[0].String.Trim();
        p.HudCallbacks.Remove(id);
        return DynValue.NewBoolean(_hud.Remove(p.Manifest.Id, id));
    }

    DynValue HudSetBar(PackState p, CallbackArguments a)
    {
        if (_hud is null) return DynValue.False;
        if (a.Count < 7) throw new ScriptRuntimeException("hud.set_bar(id, x, y, w, h, value, max[, label, color]) expects at least 7 arguments");
        if (a[0].Type != DataType.String) throw new ScriptRuntimeException("hud.set_bar: id must be a string");
        string id = a[0].String.Trim();
        double x = FiniteNum(a[1], "hud.set_bar"), y = FiniteNum(a[2], "hud.set_bar");
        double w = FiniteNum(a[3], "hud.set_bar"), h = FiniteNum(a[4], "hud.set_bar");
        double val = FiniteNum(a[5], "hud.set_bar"), max = Math.Max(0.0001, FiniteNum(a[6], "hud.set_bar"));
        string label = a.Count >= 8 && a[7].Type == DataType.String ? a[7].String.Trim() : "";
        string color = a.Count >= 9 && a[8].Type == DataType.String ? a[8].String.Trim() : "#5FD7AF";
        double pct = Math.Clamp(val / max, 0, 1);
        int filled = (int)Math.Round(pct * 10);
        string bar = "[" + new string('#', filled) + new string('-', 10 - filled) + "]";
        string text = label.Length > 0 ? $"{label} {bar} {Math.Round(val)}/{Math.Round(max)}" : $"{bar} {Math.Round(val)}/{Math.Round(max)}";
        p.HudCallbacks.Remove(id);
        return DynValue.NewBoolean(_hud.SetText(p.Manifest.Id, id, x, y, w, h, text, color));
    }

    DynValue HudSetButton(PackState p, CallbackArguments a)
    {
        if (_hud is null) return DynValue.False;
        if (a.Count < 7 || a[0].Type != DataType.String || a[5].Type != DataType.String || a[6].Type != DataType.Function)
            throw new ScriptRuntimeException("hud.set_button(id, x, y, w, h, label, on_click[, color]) expects id, 4 numbers, label, and function");
        string id = a[0].String.Trim();
        double x = FiniteNum(a[1], "hud.set_button"), y = FiniteNum(a[2], "hud.set_button");
        double w = FiniteNum(a[3], "hud.set_button"), h = FiniteNum(a[4], "hud.set_button");
        string label = a[5].String;
        string color = a.Count >= 8 && a[7].Type == DataType.String ? a[7].String.Trim() : "#F0E6D2";
        bool ok = _hud.SetButton(p.Manifest.Id, id, x, y, w, h, label, color);
        if (ok) p.HudCallbacks[id] = a[6];
        return DynValue.NewBoolean(ok);
    }

    /// <summary>Invoke a pack's HUD button callback inside its sandboxed LuaBox.</summary>
    public void DispatchHudClick(string packId, string buttonId)
    {
        if (!_packs.TryGetValue(packId, out var p) || !p.Running || p.Box is null) return;
        if (!p.HudCallbacks.TryGetValue(buttonId, out var fn)) return;
        p.ActionsThisTick = 0;
        p.Box.RawScript.Globals["__hud_fn"] = fn;
        p.Box.Eval("__hud_fn()");
        if (p.Box.Faulted)
        {
            _log($"pack '{p.Manifest.Id}' hud button '{buttonId}' faulted: {p.Box.Fault}: {p.Box.FaultDetail}");
            Stop(p.Manifest.Id);
        }
        else if (p.SaveRequested && p.CanSave)
        {
            p.SaveRequested = false;
            SavePackState(p);
        }
    }

    // ---------------------------------------------------------------- world.items (0.6.0 inventory & cats)

    DynValue ListItems(Script s, CallbackArguments a)
    {
        if (a.Count < 1) throw new ScriptRuntimeException("items.list(char) expects a character table {slot, gen}");
        var (slot, gen) = ParseSingleChar(a[0], "items.list");
        var snap = _scan?.Invoke() ?? WorldScanSnapshot.Empty;
        var arr = new Table(s);
        if (snap.Inventories is not null && snap.Inventories.TryGetValue((slot, gen), out var list))
        {
            int idx = 0;
            foreach (var it in list)
            {
                var t = new Table(s);
                t["record"] = it.Record; t["name"] = it.Name; t["type"] = it.Type;
                t["section"] = it.Section; t["qty"] = it.Qty; t["level"] = it.Level;
                arr[++idx] = t;
            }
        }
        return DynValue.NewTable(arr);
    }

    DynValue QueueItemGive(PackState p, CallbackArguments a)
    {
        if (a.Count < 2 || a[1].Type != DataType.String)
            throw new ScriptRuntimeException("items.give(char, recordId[, count, level]) expects character table and recordId");
        var (slot, gen) = ParseSingleChar(a[0], "items.give");
        string rec = a[1].String.Trim();
        if (rec.Length == 0 || rec.Length > 128) throw new ScriptRuntimeException("items.give: invalid recordId");
        int count = a.Count >= 3 && a[2].Type == DataType.Number ? (int)Math.Clamp(a[2].Number, 1, 20) : 1;
        int level = a.Count >= 4 && a[3].Type == DataType.Number ? (int)Math.Clamp(a[3].Number, 0, 100) : 0;
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("item.give", TargetId: rec, TargetSlot: slot, TargetGen: gen, Amount: count, Level: level));
        return DynValue.True;
    }

    DynValue QueueItemRemove(PackState p, CallbackArguments a)
    {
        if (a.Count < 2 || a[1].Type != DataType.String)
            throw new ScriptRuntimeException("items.remove(char, recordOrName[, count]) expects character table and item id/name");
        var (slot, gen) = ParseSingleChar(a[0], "items.remove");
        string rec = a[1].String.Trim();
        if (rec.Length == 0 || rec.Length > 128) throw new ScriptRuntimeException("items.remove: invalid item id/name");
        int count = a.Count >= 3 && a[2].Type == DataType.Number ? (int)Math.Clamp(a[2].Number, 1, 50) : 1;
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("item.remove", TargetId: rec, TargetSlot: slot, TargetGen: gen, Amount: count));
        return DynValue.True;
    }

    DynValue QueueAddCats(PackState p, CallbackArguments a)
    {
        if (a.Count < 1) throw new ScriptRuntimeException("world.add_cats(delta) expects a finite number");
        int delta = (int)Math.Clamp(Math.Round(FiniteNum(a[0], "world.add_cats")), -100_000, 100_000);
        if (delta == 0) return DynValue.True;
        if (!TrySpendAction(p)) return DynValue.False;
        _enqueueAction!(new WorldAction("world.add_cats", Amount: delta));
        return DynValue.True;
    }
}
