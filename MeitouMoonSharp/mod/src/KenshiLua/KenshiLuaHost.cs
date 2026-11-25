// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): 1:1 KenshiLua mod loader & runtime host (Milestone 1).
// Discovers <modsRoot>/<AnyMod>/scripts/init/*.lua, enforces opt-in approval (kenshilua.grants.json), runs each mod in an isolated
// sandboxed LuaBox with scoped mod-only require(), dispatches KenshiLua events (onCharsUpdate, onCombatHit, onHour, onDay),
// and cleans up MyGUI shim widgets automatically when a mod stops or faults.
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MoonSharp.Interpreter;
using PZA.Moonscript.Core;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript.KenshiLua;

public sealed record KenshiLuaModInfo(string ModId, string ModFolder, IReadOnlyList<string> InitScripts, bool IsWorkshop = false, string? ModFile = null);

public sealed class KenshiLuaHost
{
    public const long GlobalTickBudget = 2_000_000;
    public const int MaxActionsPerTick = 16;
    public const int MaxSaveBytes = 256 * 1024;

    readonly string? _modsRoot;
    readonly string? _workshopRoot;
    readonly string? _selfModFolder;
    readonly string? _grantsPath;
    readonly string? _stateDir;
    readonly Func<double?> _hours;
    readonly Func<long?> _cats;
    readonly Func<string?> _selected;
    readonly Func<WorldScanSnapshot> _scan;
    readonly Func<CameraSnap?> _camera;
    readonly Action<WorldAction> _enqueueAction;
    readonly HudState? _hud;
    readonly Func<(int W, int H)> _viewSize;
    readonly Func<ScriptLimits> _limits;
    readonly Action<string, Ink> _out;
    readonly Action<string> _log;
    readonly Stopwatch _frameClock = Stopwatch.StartNew();
    double _lastFrameSec;

    readonly Dictionary<string, ModRuntime> _mods = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);
    ulong _seed = 7001;

    sealed class ModRuntime(KenshiLuaModInfo info)
    {
        public KenshiLuaModInfo Info { get; } = info;
        public LuaBox? Box;
        public bool Running;
        public string? Error;
        public int ActionsThisTick;
        public string? LastSelectedName;
        public HashSet<string> SeenBuildings { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> HudCallbacks { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, DynValue> RequireCache { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public KenshiLuaHost(
        string? modsRoot, string? selfModFolder, string? grantsPath,
        Func<double?> hours, Func<long?> cats, Func<string?> selected,
        Func<WorldScanSnapshot> scan, Func<CameraSnap?> camera,
        Action<WorldAction> enqueueAction, HudState? hud,
        Func<ScriptLimits> limits, Action<string, Ink> output, Action<string> log,
        string? stateDir = null, string? workshopRoot = null, Func<(int W, int H)>? viewSize = null)
    {
        _modsRoot = modsRoot; _selfModFolder = selfModFolder; _grantsPath = grantsPath; _stateDir = stateDir;
        _workshopRoot = workshopRoot ?? DeriveWorkshopRoot(modsRoot);
        _hours = hours; _cats = cats; _selected = selected;
        _scan = scan; _camera = camera; _enqueueAction = enqueueAction; _hud = hud;
        _viewSize = viewSize ?? (() => (1920, 1080));
        _limits = limits; _out = output; _log = log;
        LoadGrants();
        Discover();
    }

    static string? DeriveWorkshopRoot(string? modsRoot)
    {
        if (string.IsNullOrEmpty(modsRoot)) return null;
        try
        {
            string candidate = Path.GetFullPath(Path.Combine(modsRoot, "..", "..", "..", "workshop", "content", "233860"));
            return Directory.Exists(candidate) ? candidate : null;
        }
        catch (Exception) { return null; }
    }

    void LoadGrants()
    {
        if (string.IsNullOrEmpty(_grantsPath) || !File.Exists(_grantsPath)) return;
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_grantsPath));
            if (list is null) return;
            foreach (var id in list) if (!string.IsNullOrWhiteSpace(id)) _approved.Add(id.Trim());
        }
        catch (Exception e) { _log($"kenshilua.grants.json ignored: {e.Message}"); }
    }

    void SaveGrants()
    {
        if (string.IsNullOrEmpty(_grantsPath)) return;
        try
        {
            var sorted = _approved.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            File.WriteAllText(_grantsPath, JsonSerializer.Serialize(sorted, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) { _log($"kenshilua.grants.json could not be saved: {e.Message}"); }
    }

    public int Discover()
    {
        int found = 0;
        found += DiscoverRoot(_modsRoot, isWorkshop: false);
        found += DiscoverRoot(_workshopRoot, isWorkshop: true);
        return found;
    }

    int DiscoverRoot(string? root, bool isWorkshop)
    {
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;
        int found = 0;
        foreach (var modDir in Directory.GetDirectories(root).OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string folderName = Path.GetFileName(modDir);
            if (!isWorkshop && !string.IsNullOrEmpty(_selfModFolder) && folderName.Equals(_selfModFolder, StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                if ((File.GetAttributes(modDir) & FileAttributes.ReparsePoint) != 0) continue;
                string initDir = Path.Combine(modDir, "scripts", "init");
                if (!Directory.Exists(initDir) || (File.GetAttributes(initDir) & FileAttributes.ReparsePoint) != 0) continue;
                var scripts = Directory.GetFiles(initDir, "*.lua")
                    .Where(f => (File.GetAttributes(f) & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => Path.GetRelativePath(modDir, f).Replace('\\', '/'))
                    .ToList();
                if (scripts.Count == 0) continue;
                string modName = folderName;
                string? modFileName = null;
                var modFile = Directory.GetFiles(modDir, "*.mod").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
                if (modFile is not null)
                {
                    modFileName = Path.GetFileName(modFile);
                    if (isWorkshop) modName = Path.GetFileNameWithoutExtension(modFile).Trim();
                }
                if (isWorkshop && modName.Equals("KenshiLua", StringComparison.OrdinalIgnoreCase)) continue;
                if (!_mods.ContainsKey(modName))
                    _mods[modName] = new ModRuntime(new KenshiLuaModInfo(modName, modDir, scripts, isWorkshop, modFileName));
                found++;
            }
            catch (Exception) { }
        }
        return found;
    }

    public IReadOnlyList<(KenshiLuaModInfo Info, bool Approved, bool Running, string? Error)> List() =>
        _mods.Values.OrderBy(m => m.Info.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(m => (m.Info, _approved.Contains(m.Info.ModId), m.Running, m.Error)).ToList();

    public (bool ok, string message) Allow(string modId)
    {
        Discover();
        if (!_mods.TryGetValue(modId, out var m)) return (false, $"unknown KenshiLua mod '{modId}' (see /kl list)");
        _approved.Add(m.Info.ModId);
        SaveGrants();
        return (true, $"approved KenshiLua mod '{m.Info.ModId}' (run /kl start {m.Info.ModId})");
    }

    public (bool ok, string message) Revoke(string modId)
    {
        if (!_mods.TryGetValue(modId, out var m)) return (false, $"unknown KenshiLua mod '{modId}'");
        _approved.Remove(m.Info.ModId);
        SaveGrants();
        if (m.Running) Stop(modId);
        return (true, $"revoked KenshiLua mod '{m.Info.ModId}'");
    }

    static string HudOwner(string modId) => "kl:" + modId;

    public (bool ok, string message) Start(string modId) => StartCore(modId, autoApproved: false);

    /// <summary>Auto-start every discovered KenshiLua mod whose .mod file is enabled in Kenshi's data/mods.cfg.</summary>
    public IReadOnlyList<string> StartEnabledFromModsCfg(string? modsCfgPath = null)
    {
        Discover();
        string? cfgPath = modsCfgPath;
        if (string.IsNullOrEmpty(cfgPath) && !string.IsNullOrEmpty(_modsRoot))
        {
            try { cfgPath = Path.GetFullPath(Path.Combine(_modsRoot, "..", "data", "mods.cfg")); }
            catch (Exception) { cfgPath = null; }
        }
        if (string.IsNullOrEmpty(cfgPath) || !File.Exists(cfgPath)) return [];

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var rawLine in File.ReadAllLines(cfgPath))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                enabled.Add(line);
                if (line.EndsWith(".mod", StringComparison.OrdinalIgnoreCase))
                    enabled.Add(line[..^4]);
            }
        }
        catch (Exception e)
        {
            _log($"KenshiLua: could not read mods.cfg ({e.Message})");
            return [];
        }

        var started = new List<string>();
        foreach (var m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            bool isEnabled = enabled.Contains(m.Info.ModId)
                || (!string.IsNullOrEmpty(m.Info.ModFile) && enabled.Contains(m.Info.ModFile));
            if (!isEnabled) continue;
            var (ok, msg) = StartCore(m.Info.ModId, autoApproved: true);
            if (ok) started.Add(m.Info.ModId);
            else _log($"KenshiLua auto-start failed for '{m.Info.ModId}': {msg}");
        }
        return started;
    }

    (bool ok, string message) StartCore(string modId, bool autoApproved)
    {
        Discover();
        if (!_mods.TryGetValue(modId, out var m)) return (false, $"unknown KenshiLua mod '{modId}' (see /kl list)");
        if (m.Running) return (true, $"KenshiLua mod '{m.Info.ModId}' is already running");
        if (!autoApproved && !_approved.Contains(m.Info.ModId))
            return (false, $"KenshiLua mod '{m.Info.ModId}' needs approval: run /kl allow {m.Info.ModId}");

        var baseLim = _limits();
        var klLim = new ScriptLimits
        {
            InstructionsPerCall = Math.Max(baseLim.InstructionsPerCall, 600_000),
            InstructionsPerTick = Math.Max(baseLim.InstructionsPerTick, 2_000_000),
        };
        var box = new LuaBox("kl:" + m.Info.ModId, klLim, _seed++, s => _out($"[KL:{m.Info.ModId}] {s}", Ink.Text), _log) { Forgiving = true };
        m.Box = box;
        m.Error = null;
        m.ActionsThisTick = 0;
        m.HudCallbacks.Clear();
        m.RequireCache.Clear();

        IReadOnlyList<string> ActiveModNames() => _mods.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        bool SpendAction()
        {
            if (m.ActionsThisTick >= MaxActionsPerTick) return false;
            m.ActionsThisTick++;
            return true;
        }

        KenshiLuaApi.Install(box, m.Info.ModId, _hours, _cats, _selected, _scan, _camera, ActiveModNames, _enqueueAction, SpendAction, _log, _out, m.Info.ModFolder);
        MyGuiShim.Install(box, HudOwner(m.Info.ModId), _hud, m.HudCallbacks, m.Info.ModFolder, _viewSize);
        box.Expose("", "require", a => ScopedRequire(m, a));

        foreach (var rel in m.Info.InitScripts)
        {
            string? full = HostedScript.SafePath(m.Info.ModFolder, rel);
            if (full is null || !File.Exists(full)) continue;
            string text;
            try { text = File.ReadAllText(full); }
            catch (Exception e) { m.Error = e.Message; Stop(modId); return (false, $"KenshiLua '{modId}': {e.Message}"); }
            box.Load(rel, text);
            if (box.Faulted || box.LastError is not null)
            {
                m.Error = box.Faulted ? $"{box.Fault}: {box.FaultDetail}" : box.LastError;
                Stop(modId);
                return (false, $"KenshiLua '{modId}' failed in {rel}: {m.Error}");
            }
        }

        m.Running = true;
        m.SeenBuildings.Clear();
        m.LastSelectedName = null;
        FirePlayerLoad(m);
        return (true, $"started KenshiLua mod '{m.Info.ModId}' ({m.Info.InitScripts.Count} init script(s))");
    }

    public (bool ok, string message) Stop(string modId)
    {
        if (!_mods.TryGetValue(modId, out var m)) return (false, $"unknown KenshiLua mod '{modId}'");
        if (m.Running && m.Box is { Faulted: false }) FirePlayerSerialise(m);
        _hud?.ClearPack(HudOwner(m.Info.ModId));
        m.HudCallbacks.Clear();
        m.RequireCache.Clear();
        m.SeenBuildings.Clear();
        m.LastSelectedName = null;
        m.Box = null;
        m.Running = false;
        return (true, $"stopped KenshiLua mod '{m.Info.ModId}'");
    }

    string? ModSavePath(string modId) =>
        string.IsNullOrEmpty(_stateDir) ? null : HostedScript.SafePath(_stateDir, "kl_" + modId.ToLowerInvariant() + ".json");

    void FirePlayerLoad(ModRuntime m)
    {
        if (m.Box is null || m.Box.Faulted) return;
        string raw = "{}";
        string? path = ModSavePath(m.Info.ModId);
        if (path is not null && File.Exists(path))
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Length > 0 && fi.Length <= MaxSaveBytes) raw = File.ReadAllText(path);
            }
            catch (Exception) { }
        }
        m.Box.RawScript.Globals["__kl_gd_raw"] = DynValue.NewString(raw);
        m.Box.Eval("__kl_dispatch('onPlayerLoadFromSerialise', getGameWorld().player, __kl_make_gamedata(__kl_gd_raw))");
    }

    void FirePlayerSerialise(ModRuntime m)
    {
        if (m.Box is null || m.Box.Faulted) return;
        string? path = ModSavePath(m.Info.ModId);
        if (path is null) return;
        try
        {
            var res = m.Box.Eval("local gd = __kl_make_gamedata('{}'); __kl_dispatch('onPlayerSerialise', getGameWorld().player, gd); return json.encode(gd:toTable())");
            if (res.Type == DataType.String && res.String.Length > 2 && res.String.Length <= MaxSaveBytes)
            {
                Directory.CreateDirectory(_stateDir!);
                File.WriteAllText(path, res.String);
            }
        }
        catch (Exception e) { _log($"KenshiLua '{m.Info.ModId}' save failed: {e.Message}"); }
    }

    DynValue ScopedRequire(ModRuntime m, CallbackArguments a)
    {
        if (m.Box is null || a.Count < 1 || a[0].Type != DataType.String)
            throw new ScriptRuntimeException("require(name) expects a module name string");
        string modName = a[0].String.Trim();
        if (m.RequireCache.TryGetValue(modName, out var cached)) return cached;
        string relPath = modName.Replace('.', '/') + ".lua";
        string? full = HostedScript.SafePath(m.Info.ModFolder, "scripts/" + relPath)
                    ?? HostedScript.SafePath(m.Info.ModFolder, "lib/" + relPath)
                    ?? HostedScript.SafePath(m.Info.ModFolder, relPath);
        if (full is null || !File.Exists(full))
            throw new ScriptRuntimeException($"require: module '{modName}' not found inside mod '{m.Info.ModId}'");
        string code = File.ReadAllText(full);
        var res = m.Box.RawScript.DoString(code, null, modName);
        if (res.IsNil()) res = DynValue.True;
        m.RequireCache[modName] = res;
        return res;
    }

    public void Tick(IReadOnlyList<GameEvent>? events = null)
    {
        double nowSec = _frameClock.Elapsed.TotalSeconds;
        double dt = _lastFrameSec > 0 ? Math.Clamp(nowSec - _lastFrameSec, 0.001, 0.1) : 0.016;
        _lastFrameSec = nowSec;
        string dtStr = dt.ToString("0.#####", CultureInfo.InvariantCulture);

        long used = 0;
        var snap = _scan();
        string? selName = _selected();
        foreach (var m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Box is null) continue;
            m.ActionsThisTick = 0;
            if (used >= GlobalTickBudget) break;

            // Fire MyGUI::Gui::eventFrameStart (used by POSH Damage Numbers, Nameplates, and other frame-driven KenshiLua mods)
            m.Box.Eval($"__kl_dispatch('MyGUI::Gui::eventFrameStart', {dtStr})");
            used += m.Box.LastCallInstructions;
            if (m.Box.Faulted)
            {
                _log($"KenshiLua '{m.Info.ModId}' faulted in eventFrameStart: {m.Box.Fault}: {m.Box.FaultDetail}");
                Stop(m.Info.ModId);
                continue;
            }
            if (!m.Running || m.Box is null || used >= GlobalTickBudget) continue;

            // Fire onBuildingLoaded for any newly-discovered door/building
            foreach (var d in snap.Doors)
            {
                if (!m.Running || m.Box is null || used >= GlobalTickBudget) break;
                if (!m.SeenBuildings.Add(d.Building)) continue;
                var bt = new Table(m.Box.RawScript);
                bt["id"] = d.Id; bt["building"] = d.Building; bt["name"] = d.Building;
                bt["owner"] = d.Owner; bt["is_player"] = d.IsPlayer;
                bt["x"] = d.X; bt["y"] = d.Y; bt["z"] = d.Z;
                bt["open_amount"] = d.OpenAmount; bt["motion"] = d.Motion;
                bt["locked"] = d.Locked; bt["broken"] = d.Broken; bt["is_gate"] = d.IsGate;
                m.Box.RawScript.Globals["__kl_bld"] = DynValue.NewTable(bt);
                m.Box.Eval("__kl_dispatch('onBuildingLoaded', __kl_wrap_building(__kl_bld))");
                used += m.Box.LastCallInstructions;
                if (m.Box.Faulted) { Stop(m.Info.ModId); break; }
            }
            if (!m.Running || m.Box is null || used >= GlobalTickBudget) continue;

            // Fire onCharacterSelect when selected character changes
            if (!string.IsNullOrEmpty(selName) && !string.Equals(selName, m.LastSelectedName, StringComparison.Ordinal))
            {
                m.LastSelectedName = selName;
                var selChar = snap.Characters.FirstOrDefault(c => string.Equals(c.Name, selName, StringComparison.OrdinalIgnoreCase));
                if (selChar is not null)
                {
                    var ct = new Table(m.Box.RawScript);
                    ct["slot"] = selChar.Slot; ct["gen"] = selChar.Gen; ct["name"] = selChar.Name;
                    ct["faction"] = selChar.Faction; ct["is_player"] = selChar.IsPlayer;
                    ct["x"] = selChar.X; ct["y"] = selChar.Y; ct["z"] = selChar.Z;
                    ct["hp"] = selChar.Hp; ct["down"] = selChar.Down; ct["dead"] = selChar.Dead;
                    ct["in_combat"] = selChar.InCombat; ct["squad_id"] = selChar.SquadId;
                    m.Box.RawScript.Globals["__kl_sel"] = DynValue.NewTable(ct);
                    m.Box.Eval("__kl_dispatch('onCharacterSelect', __kl_wrap_char(__kl_sel))");
                    used += m.Box.LastCallInstructions;
                    if (m.Box.Faulted) { Stop(m.Info.ModId); continue; }
                }
            }

            m.Box.Eval("__kl_dispatch('onCharsUpdate', getGameWorld())");
            used += m.Box.LastCallInstructions;
            if (m.Box.Faulted)
            {
                _log($"KenshiLua '{m.Info.ModId}' faulted: {m.Box.Fault}: {m.Box.FaultDetail}");
                Stop(m.Info.ModId);
                continue;
            }
            if (events is null || events.Count == 0) continue;
            foreach (var ev in events)
            {
                if (!m.Running || m.Box is null || used >= GlobalTickBudget) break;
                string klName = ev.Name switch { "hit" => "onCombatHit", "hour" => "onHour", "day" => "onDay", "character_down" => "onCharacterDown", _ => "" };
                if (klName.Length == 0) continue;
                var tbl = new Table(m.Box.RawScript);
                foreach (var kv in ev.Payload)
                    tbl[kv.Key] = kv.Value switch
                    {
                        null => DynValue.Nil, bool b => DynValue.NewBoolean(b), int i => DynValue.NewNumber(i),
                        long l => DynValue.NewNumber(l), double d => DynValue.NewNumber(d), float f => DynValue.NewNumber(f),
                        string s => DynValue.NewString(s), _ => DynValue.NewString(kv.Value.ToString() ?? ""),
                    };
                m.Box.RawScript.Globals["__kl_ev"] = DynValue.NewTable(tbl);
                m.Box.Eval($"__kl_dispatch('{klName}', __kl_ev)");
                used += m.Box.LastCallInstructions;
                if (m.Box.Faulted) { Stop(m.Info.ModId); break; }
            }
        }
    }

    public void DispatchHudClick(string ownerKey, string buttonId)
    {
        if (!ownerKey.StartsWith("kl:", StringComparison.OrdinalIgnoreCase)) return;
        string modId = ownerKey[3..];
        if (!_mods.TryGetValue(modId, out var m) || !m.Running || m.Box is null) return;
        if (!m.HudCallbacks.TryGetValue(buttonId, out var fn)) return;
        m.ActionsThisTick = 0;
        m.Box.RawScript.Globals["__kl_btn_fn"] = fn;
        m.Box.Eval("__kl_btn_fn()");
        if (m.Box.Faulted)
        {
            _log($"KenshiLua '{m.Info.ModId}' MyGUI button '{buttonId}' faulted: {m.Box.Fault}: {m.Box.FaultDetail}");
            Stop(m.Info.ModId);
        }
    }

    /// <summary>Forward a key-down event to running KenshiLua mods (never called while a text box has keyboard focus).</summary>
    public void NotifyKeyDown(int keyCode)
    {
        foreach (var m in _mods.Values.OrderBy(x => x.Info.ModId, StringComparer.OrdinalIgnoreCase))
        {
            if (!m.Running || m.Box is null) continue;
            m.ActionsThisTick = 0;
            m.Box.RawScript.Globals["__kl_key"] = DynValue.NewNumber(keyCode);
            m.Box.Eval("__kl_dispatch('onKeyDown', __kl_key)");
            if (m.Box.Faulted)
            {
                _log($"KenshiLua '{m.Info.ModId}' onKeyDown faulted: {m.Box.Fault}: {m.Box.FaultDetail}");
                Stop(m.Info.ModId);
            }
        }
    }
}
