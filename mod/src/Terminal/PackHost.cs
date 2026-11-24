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

    public static readonly IReadOnlyList<string> DefaultPermissions = ["commands", "timers", "world.read"];

    public static readonly HashSet<string> SafePermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "commands", "timers", "events", "world.read",
    };

    static readonly HashSet<string> ReservedCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "run", "reload", "scripts", "config", "reset", "clear", "battle", "crawlers", "packs", "start", "stop", "restart", "allow", "revoke",
    };

    public static readonly HashSet<string> AllowedEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "day", "hour", "hit", "character_down",
    };

    readonly string _packsDir;
    readonly string? _modsRoot;
    readonly string? _selfModFolderName;
    readonly string? _grantsPath;
    readonly Func<double?> _hours;
    readonly Func<string?> _selected;
    readonly Func<IReadOnlyList<FighterSnap>> _squad;
    readonly Func<long?> _cats;
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
        public List<string> Commands { get; } = [];
        public List<TimerEntry> Timers { get; } = [];
        public Dictionary<string, List<DynValue>> Events { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public PackHost(string packsDir, Func<double?> hours, Func<string?> selected, Func<IReadOnlyList<FighterSnap>> squad,
                    Func<long?> cats, Func<ScriptLimits> limits, Action<string, Ink> output, Action<string> log,
                    string? modsRoot = null, string? selfModFolderName = null, string? grantsPath = null)
    {
        _packsDir = packsDir; _modsRoot = modsRoot; _selfModFolderName = selfModFolderName; _grantsPath = grantsPath;
        _hours = hours; _selected = selected; _squad = squad; _cats = cats;
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
                foreach (var c in caps ?? []) if (SafePermissions.Contains(c)) set.Add(c.ToLowerInvariant());
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
        var unsupported = req.Where(c => !SafePermissions.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
        if (!SafePermissions.Contains(cap))
            return (false, $"unknown or unsupported permission '{cap}' (known: {string.Join(", ", SafePermissions.OrderBy(x => x))})");
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
        var unsupported = reqPerms.Where(c => !SafePermissions.Contains(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        p.Box = box;
        p.Error = null;
        p.Commands.Clear();
        p.Timers.Clear();
        p.Events.Clear();
        box.Load(p.Manifest.Entry, text);
        if (box.Faulted || box.LastError is not null)
        {
            p.Error = box.Faulted ? $"{box.Fault}: {box.FaultDetail}" : box.LastError;
            Stop(id);
            return (false, $"pack '{id}' failed: {p.Error}");
        }
        p.Running = true;
        string cmds = p.Commands.Count > 0 ? " (commands: " + string.Join(", ", p.Commands.Select(c => "/" + c)) + ")" : "";
        return (true, $"started pack '{id}' v{p.Manifest.Version}{cmds}");
    }

    public (bool ok, string message) Stop(string id)
    {
        if (!_packs.TryGetValue(id, out var p)) return (false, $"unknown pack '{id}'");
        var mine = _commands.Where(kv => ReferenceEquals(kv.Value.Pack, p)).Select(kv => kv.Key).ToList();
        foreach (var k in mine) _commands.Remove(k);
        p.Commands.Clear();
        p.Timers.Clear();
        p.Events.Clear();
        p.Box = null;
        p.Running = false;
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
        return true;
    }

    /// <summary>Advance deterministic game-clock timers and deliver queued game events under a single global per-tick budget.</summary>
    public void Tick(double? gameHours, IReadOnlyList<GameEvent>? events = null)
    {
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
}
