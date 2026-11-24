// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): game-free Lua REPL session.
//
// The terminal's script is UI side: it is not part of the world, never saved and not in the state hash. Its API is tiny and read-only:
//   print(...)          a line in the terminal
//   world.hour()        the world's hour of day (0..24) from the host's last tick snapshot, nil before a world runs
//   world.day()         the day number as the HUD shows it (World.GameHours counts day 1 as hours 24-47: day = floor(hours / 24))
//   world.cats()        the player faction's money in cats from the last tick snapshot, nil before a world runs
//   world.squad()       a 1-indexed array of FighterSnap tables (selected first, then active squadmates, up to 8)
//   selected.name()     the first selected character's name from the same snapshot, nil when none
//   selected.info()     the first selected character's FighterSnap table (stats, HP, race, weapon, armour), nil when none
// Scripts: <host folder>/scripts/*.lua, read again at every /run (the hot reload the terminal gives you).
using System.Globalization;
using MoonSharp.Interpreter;
using PZA.Moonscript.Core;

namespace PZA.Moonscript.Repl;

/// <summary>Read-only character snapshot copied in SlowWorld for UI-side terminal scripts (Slice 2).</summary>
public sealed record FighterSnap(
    string Name,
    string Race,
    bool Robot,
    double Hp,
    double MaxHp,
    double Bleeding,
    bool Down,
    double Str,
    double Dex,
    double Tough,
    double Atk,
    double Def,
    double Med,
    double Robotics,
    double Ma,
    double Dodge,
    string Weapon,
    int WeaponLevel,
    bool Ranged,
    int ArmourCount,
    double ArmourLevel);

public sealed class ReplSession : IReplHost
{
    readonly string _scripts;
    readonly Func<double?> _hours;
    readonly Func<string?> _selected;
    readonly Func<IReadOnlyList<ConfigEntry>> _config;
    readonly Func<ScriptLimits> _limits;
    readonly Action<string> _log;
    readonly Func<IReadOnlyList<FighterSnap>> _squad;
    readonly Func<long?> _cats;
    LuaBox _box = null!;
    public ReplCore Core { get; }
    public int Resets { get; private set; }

    public ReplSession(string scriptsFolder, Func<double?> hours, Func<string?> selected, Func<IReadOnlyList<ConfigEntry>> config,
                           Func<ScriptLimits> limits, Action<string> log, bool banner = true,
                           Func<IReadOnlyList<FighterSnap>>? squad = null, Func<long?>? cats = null)
    {
        _scripts = scriptsFolder; _hours = hours; _selected = selected; _config = config; _limits = limits; _log = log;
        _squad = squad ?? (() => []);
        _cats = cats ?? (() => null);
        Core = new ReplCore(this, banner);
        ResetLua();
    }

    public LuaBox Box => _box;

    public void ResetLua()
    {
        _box = new LuaBox("terminal", _limits(), 1, s => Core.Out(s, Ink.Text), _log) { Forgiving = true };
        _box.Expose("world", "hour", _ => _hours() is double h ? DynValue.NewNumber(Math.Floor(((h % 24) + 24) % 24 * 100) / 100) : DynValue.Nil);
        _box.Expose("world", "day", _ => _hours() is double h ? DynValue.NewNumber(Math.Max(1, Math.Floor(h / 24))) : DynValue.Nil);
        _box.Expose("world", "cats", _ => _cats() is long c ? DynValue.NewNumber(c) : DynValue.Nil);
        _box.Expose("world", "squad", _ => ToLuaSquad(_box.RawScript, _squad()));
        _box.Expose("selected", "name", _ => _selected() is { Length: > 0 } n ? DynValue.NewString(n) : DynValue.Nil);
        _box.Expose("selected", "info", _ =>
        {
            var list = _squad();
            return list.Count > 0 ? ToLuaFighter(_box.RawScript, list[0]) : DynValue.Nil;
        });
        _box.Load("terminal", "");
        Resets++;
    }

    internal static DynValue ToLuaSquad(Script s, IReadOnlyList<FighterSnap> squad)
    {
        var t = new Table(s);
        int n = Math.Min(squad.Count, 8);
        for (int i = 0; i < n; i++) t[i + 1] = ToLuaFighter(s, squad[i]);
        return DynValue.NewTable(t);
    }

    static DynValue ToLuaFighter(Script s, FighterSnap f)
    {
        var t = new Table(s);
        t["name"] = f.Name;
        t["race"] = f.Race;
        t["robot"] = f.Robot;
        t["hp"] = Math.Round(f.Hp, 1);
        t["max_hp"] = Math.Round(f.MaxHp, 1);
        t["bleeding"] = Math.Round(f.Bleeding, 2);
        t["down"] = f.Down;
        t["str"] = Math.Round(f.Str, 1);
        t["dex"] = Math.Round(f.Dex, 1);
        t["tough"] = Math.Round(f.Tough, 1);
        t["atk"] = Math.Round(f.Atk, 1);
        t["def"] = Math.Round(f.Def, 1);
        t["med"] = Math.Round(f.Med, 1);
        t["robotics"] = Math.Round(f.Robotics, 1);
        t["ma"] = Math.Round(f.Ma, 1);
        t["dodge"] = Math.Round(f.Dodge, 1);
        t["weapon"] = f.Weapon;
        t["weapon_level"] = f.WeaponLevel;
        t["ranged"] = f.Ranged;
        t["armour"] = f.ArmourCount;
        t["armour_level"] = Math.Round(f.ArmourLevel, 1);
        return DynValue.NewTable(t);
    }

    public (string? result, string? error, long instructions) Run(string code, string chunk)
    {
        var r = chunk == "terminal" ? _box.Eval(code) : _box.Eval(code);
        long n = _box.LastCallInstructions;
        if (_box.Faulted)
        {
            string why = $"{_box.Fault}: {_box.FaultDetail} (the Lua state was reset)";
            ResetLua();
            return (null, why, n);
        }
        if (_box.LastError != null) return (null, _box.LastError, n);
        string? text = r.IsNil() || r.Type == MoonSharp.Interpreter.DataType.Void ? null : Show(r);
        return (text, null, n);
    }

    static string Show(MoonSharp.Interpreter.DynValue v) => v.Type == MoonSharp.Interpreter.DataType.Tuple
        ? string.Join("\t", v.Tuple.Select(SafeLib.Plain))
        : v.Type == MoonSharp.Interpreter.DataType.String ? v.String : SafeLib.Plain(v);

    public string? CompileError(string code) => _box.CompileError(code);

    public IReadOnlyList<string> Scripts()
    {
        try
        {
            if (!Directory.Exists(_scripts)) return [];
            return Directory.GetFiles(_scripts, "*.lua").Select(Path.GetFileNameWithoutExtension).OfType<string>()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { return []; }
    }

    public (string? text, string? error) ReadScript(string name)
    {
        string file = name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? name : name + ".lua";
        var path = HostedScript.SafePath(_scripts, file);
        if (path is null) return (null, $"'{name}' is not a script in the scripts folder");
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (null, $"no script '{name}' (see /scripts)");
            if (fi.Length > HostedScript.MaxFileBytes) return (null, $"'{name}' is larger than {HostedScript.MaxFileBytes} bytes");
            return (File.ReadAllText(path), null);
        }
        catch (Exception e) { return (null, $"'{name}': {e.GetType().Name}"); }
    }

    public IReadOnlyList<ConfigEntry> Config() => _config();

    /// <summary>A number setting for /config: parsed invariant, clamped to its range.</summary>
    public static ConfigEntry Number(string key, string description, Func<double> get, Action<double> set, double min, double max) =>
        new(key, description, () => get().ToString("0.##", CultureInfo.InvariantCulture), v =>
        {
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || !double.IsFinite(d)) return "not a number";
            set(Math.Clamp(d, min, max));
            return null;
        });

    public static ConfigEntry Switch(string key, string description, Func<bool> get, Action<bool> set) =>
        new(key, description, () => get() ? "on" : "off", v =>
        {
            switch (v.ToLowerInvariant()) { case "on" or "true" or "1" or "yes": set(true); return null; case "off" or "false" or "0" or "no": set(false); return null; }
            return "on or off";
        });
}
