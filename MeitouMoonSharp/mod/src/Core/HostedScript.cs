// MeitouMoonscript: one script as other mods see it (IScript), with hot reload. Author: PZA. Licence: GPLv3 or later.
//
// Claim C9 (hot reload is outside the tick): a reload swaps the box only in Reload() (host or UI side) or at BeginTick (before a tick's first
// call), never between two calls of a tick; the new box is the new text loaded and the old save restored, exactly what a cold start with the
// same text and state gives (fuzz target host.reload compares the hashes). A new text that does not load leaves the old box running.
// File reads stay inside the mod's folder (fuzz target host.path): no "..", no rooted path, no other drive, at most MaxFileBytes.
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public sealed class HostedScript : IScript
{
    public const long MaxFileBytes = 1 << 20;

    readonly ScriptSpec _spec;
    readonly Action<string> _log;
    LuaBox _box;
    DateTime _stamp;
    long _tick;

    public string Name => _spec.Name;
    public string? Fault => _box.Fault;
    public string? FaultDetail => _box.FaultDetail;
    public long FaultTick => _box.FaultTick;
    public long LastCallInstructions => _box.LastCallInstructions;
    public string? ReloadNote { get; private set; }
    public int Reloads { get; private set; }
    public LuaBox Box => _box;   // tests and the terminal only

    public HostedScript(ScriptSpec spec, Action<string> log)
    {
        _spec = spec;
        _log = log;
        var (text, error) = ReadText();
        _box = Make(text ?? "", error);
    }

    LuaBox Make(string text, string? readError)
    {
        var box = new LuaBox(_spec.Owner + "/" + _spec.Name, _spec.Limits ?? new ScriptLimits(), _spec.Seed,
                             _spec.Print ?? (s => _log($"[{_spec.Owner}/{_spec.Name}] {s}")), _log);
        foreach (var f in _spec.Functions)
            box.Expose(f.Table, f.Name, a => f.Call(a.GetArray().Select(ScriptValue.FromLua).ToList()).ToLua(box.RawScript));
        box.BeginTick(_tick);
        if (readError != null) box.Load(_spec.Name, "error(" + Quote(readError) + ")");
        else box.Load(_spec.Name, text);
        return box;
    }

    static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ") + "\"";

    /// <summary>The script's text, or why it cannot be read.</summary>
    (string? text, string? error) ReadText()
    {
        if (_spec.Text != null) return (_spec.Text, null);
        if (_spec.Folder == null || _spec.File == null) return (null, "no script: give Text, or Folder and File");
        var path = SafePath(_spec.Folder, _spec.File);
        if (path == null) return (null, $"'{_spec.File}' is not a file inside the mod's folder");
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return (null, $"'{_spec.File}' not found");
            if (fi.Length > MaxFileBytes) return (null, $"'{_spec.File}' is larger than {MaxFileBytes} bytes");
            _stamp = fi.LastWriteTimeUtc;
            return (File.ReadAllText(path), null);
        }
        catch (Exception e) { return (null, $"'{_spec.File}': {e.GetType().Name}"); }
    }

    /// <summary>The full path of `relative` inside `folder`, or null when it would leave it (claim of fuzz target host.path).</summary>
    public static string? SafePath(string folder, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 260 || Path.IsPathRooted(relative)) return null;
        if (relative.Contains(':') || relative.Contains('\0')) return null;
        foreach (var part in relative.Split('/', '\\'))
            if (part == ".." || part.Trim().Length == 0 && part.Length > 0) return null;
        string root, full;
        try
        {
            root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            full = Path.GetFullPath(Path.Combine(root, relative));
        }
        catch (Exception) { return null; }
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || full.Length <= root.Length) return null;
        // Block symlinks / directory junctions (FileAttributes.ReparsePoint) on every segment inside root
        string cur = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var seg in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            cur = Path.Combine(cur, seg);
            try
            {
                if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                    return null;
            }
            catch (Exception) { return null; }
        }
        return full;
    }

    public void BeginTick(long tick)
    {
        _tick = tick;
        if (_spec.HotReload && _spec.File != null && Changed()) Reload();
        _box.BeginTick(tick);
    }

    bool Changed()
    {
        var path = _spec.Folder == null ? null : SafePath(_spec.Folder, _spec.File!);
        try { return path != null && File.Exists(path) && File.GetLastWriteTimeUtc(path) != _stamp; }
        catch (Exception) { return false; }
    }

    public bool Reload()
    {
        var (text, error) = ReadText();
        if (text == null) { ReloadNote = "reload refused: " + error; _log($"{_spec.Owner}/{_spec.Name}: {ReloadNote}"); return false; }
        var saved = _box.Faulted ? null : _box.Save();
        var fresh = Make(text, null);
        if (fresh.Faulted || (saved != null && !fresh.Restore(saved)))
        {
            ReloadNote = $"reload refused, the old script keeps running: {fresh.Fault}: {fresh.FaultDetail}";
            _log($"{_spec.Owner}/{_spec.Name}: {ReloadNote}");
            return false;
        }
        _box = fresh;
        Reloads++;
        ReloadNote = saved == null ? "reloaded (fresh state: the old script was switched off)" : "reloaded, state kept";
        _log($"{_spec.Owner}/{_spec.Name}: {ReloadNote}");
        return true;
    }

    public bool Has(string function) => !_box.Faulted && _box.RawScript.Globals.RawGet(function) is { Type: DataType.Function };

    public ScriptValue Call(string function, params ScriptValue[] args)
    {
        var s = _box.RawScript;
        var r = _box.Call(function, args.Select(a => a.ToLua(s)).ToArray());
        return LuaThread.Shared.Invoke(() => ScriptValue.FromLua(r));
    }

    public ScriptValue Eval(string code)
    {
        var r = _box.Eval(code);
        return LuaThread.Shared.Invoke(() => ScriptValue.FromLua(r));
    }

    public byte[] Save() => _box.Save();
    public bool Restore(byte[] saved) => _box.Restore(saved);
    public ulong StateHash() => _box.StateHash();
}
