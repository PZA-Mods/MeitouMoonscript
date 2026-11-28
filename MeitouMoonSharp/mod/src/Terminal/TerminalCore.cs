// MeitouMoonscript (Author: PZA, Licence: GPLv3 or later): game-free REPL console logic (driven by the fuzz harness).
//
// A line typed at the prompt is a slash command or Lua. Output is queued and revealed a few characters a frame (the typing animation);
// while anything is queued the status line spins. Rules the fuzz target terminal.drive checks:
//   - the scrollback never holds more than MaxLines lines and no line longer than MaxLineChars;
//   - every queued character is shown within ceil(queued / speed) frames, in order, and nothing typed is lost or doubled;
//   - Lua runs only through the terminal's own sandboxed script (never a slash command's argument as code, except /run's file);
//   - /config set changes only a listed key, to a value its own parser accepts.
using System.Globalization;
using System.Text;

namespace PZA.Moonscript.Repl;

public enum Ink { Text, Prompt, Result, Error, Dim, Banner, Accent }

public sealed record ConfigEntry(string Key, string Description, Func<string> Get, Func<string, string?> Set);

public interface IReplHost
{
    /// <summary>Run Lua in the terminal's script; the printed lines go through Print, the result back as text (null when nothing).</summary>
    (string? result, string? error, long instructions) Run(string code, string chunk);
    /// <summary>Null when the text compiles (nothing runs), else the syntax error.</summary>
    string? CompileError(string code);
    /// <summary>The .lua files the terminal may /run (names without the extension).</summary>
    IReadOnlyList<string> Scripts();
    /// <summary>A script's text, or null with an error.</summary>
    (string? text, string? error) ReadScript(string name);
    IReadOnlyList<ConfigEntry> Config();
    /// <summary>Reset the terminal's Lua state.</summary>
    void ResetLua();
}

public sealed class ReplCore
{
    public const int MaxLines = 400, MaxLineChars = 160, MaxInput = 4096, MaxBlockLines = 64, MaxEditorChars = 16384;
    public static readonly string[] SpinnerFrames = ["-", "\\", "|", "/"];
    public static readonly string[] Banner =
    [
        "==============================================",
        "   >_    M O O N S C R I P T    T E R M I N A L",
        "==============================================",
    ];

    readonly IReplHost _host;
    readonly List<(string Text, Ink Ink)> _lines = [];
    readonly Queue<(string Text, Ink Ink)> _queue = new();
    readonly List<string> _block = [];
    string _typing = "";        // the queued line being revealed
    Ink _typingInk;
    double _shown;              // characters of _typing shown so far
    int _frame;
    public double CharsPerFrame { get; set; } = 6;
    public bool Animate { get; set; } = true;
    public string Status { get; private set; } = "ready";
    public bool Continuing => _block.Count > 0;
    public int PendingLines => _block.Count;
    public long LastInstructions { get; private set; }
    public string? LastScript { get; private set; }
    public int Changes { get; private set; }
    /// <summary>The terminal's /battle [hungry|dust|beak] (set by the host; null in tests that have no window).</summary>
    public Action<string>? OpenBattle { get; set; }
    /// <summary>The terminal's /crawlers (clickable Cat Crawlers table window).</summary>
    public Action? OpenCrawlers { get; set; }
    /// <summary>MTA:SA-style Lua pack host (optional; null in unit tests that don't load packs).</summary>
    public PackHost? Packs { get; set; }
    /// <summary>1:1 KenshiLua compat host for &lt;Mod&gt;/scripts/init/*.lua (optional).</summary>
    public PZA.Moonscript.KenshiLua.KenshiLuaHost? KenshiLua { get; set; }
    /// <summary>Meitou 0.24.0+ runs KenshiLua mods itself (Meitou.Lua): /kl then only lists and stops, so no mod runs twice.</summary>
    public bool KenshiLuaRetired { get; set; }

    public ReplCore(IReplHost host, bool banner = true)
    {
        _host = host;
        if (banner) Welcome();
    }

    public IReadOnlyList<(string Text, Ink Ink)> Lines => _lines;
    public bool Busy => _typing.Length > 0 || _queue.Count > 0;
    public string Spinner => Busy ? SpinnerFrames[_frame / 3 % SpinnerFrames.Length] : "*";
    public int Queued => _queue.Sum(q => q.Text.Length) + _typing.Length - (int)_shown;

    /// <summary>What the window shows: the committed lines and the line being typed, partly.</summary>
    public IEnumerable<(string Text, Ink Ink)> Visible()
    {
        foreach (var l in _lines) yield return l;
        if (_typing.Length > 0) yield return (_typing[..Math.Min(_typing.Length, (int)_shown)] + "_", _typingInk);
    }

    public void Welcome()
    {
        foreach (var b in Banner) Out(b, Ink.Banner);
        Out("Lua 5.2 in a sandbox: no files, no clock, a budget per call. Type /help.", Ink.Dim);
    }

    // ---------------------------------------------------------------- output

    public void Out(string text, Ink ink = Ink.Text)
    {
        foreach (var raw in (text ?? "").Replace("\r", "").Split('\n'))
            foreach (var piece in Chop(Clean(raw)))
            {
                if (_queue.Count >= MaxLines) _queue.Dequeue();   // a flood keeps the newest lines
                _queue.Enqueue((piece, ink));
            }
        if (!Animate) Flush();
    }

    static string Clean(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c == '\t' ? ' ' : c < 32 ? '?' : c);
        return sb.ToString();
    }

    static IEnumerable<string> Chop(string s)
    {
        if (s.Length == 0) { yield return ""; yield break; }
        for (int i = 0; i < s.Length; i += MaxLineChars) yield return s.Substring(i, Math.Min(MaxLineChars, s.Length - i));
    }

    void Commit(string text, Ink ink)
    {
        _lines.Add((text, ink));
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        Changes++;
    }

    /// <summary>Show everything queued at once.</summary>
    public void Flush()
    {
        if (_typing.Length > 0) Commit(_typing, _typingInk);
        _typing = ""; _shown = 0;
        while (_queue.Count > 0) { var q = _queue.Dequeue(); Commit(q.Text, q.Ink); }
    }

    /// <summary>One frame of the animation.</summary>
    public void Frame()
    {
        _frame++;
        if (!Animate) { Flush(); return; }
        double budget = Math.Max(1, CharsPerFrame);
        while (budget > 0)
        {
            if (_typing.Length == 0)
            {
                if (_queue.Count == 0) return;
                (_typing, _typingInk) = _queue.Dequeue();
                _shown = 0;
                if (_typing.Length == 0) { Commit("", _typingInk); continue; }   // an empty line costs nothing
            }
            double step = Math.Min(budget, _typing.Length - _shown);
            _shown += step; budget -= step;
            Changes++;
            if (_shown >= _typing.Length) { Commit(_typing, _typingInk); _typing = ""; _shown = 0; }
        }
    }

    // ---------------------------------------------------------------- input

    public void Submit(string input)
    {
        input = (input ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (input.Length > MaxInput) input = input[..MaxInput];
        if (input.Length == 0) return;
        Flush();
        if (_block.Count > 0 && input.Equals("/cancel", StringComparison.OrdinalIgnoreCase))
        {
            _block.Clear();
            Out("multi-line input cancelled.", Ink.Dim);
            Status = "ready";
            return;
        }
        if (_block.Count == 0 && input.StartsWith('/'))
        {
            Out("> " + input, Ink.Prompt);
            Command(input);
            return;
        }
        bool wasContinuing = _block.Count > 0;
        foreach (var ln in input.Split('\n'))
        {
            Out((wasContinuing ? ".. " : "> ") + ln, Ink.Prompt);
            wasContinuing = true;
        }
        string candidate = _block.Count > 0 ? string.Join("\n", _block) + "\n" + input : input;
        if (_block.Count < MaxBlockLines && _host.CompileError("return " + candidate) is not null
            && _host.CompileError(candidate) is { } err && IsIncompleteLua(err))
        {
            _block.Add(input);
            Status = $"... ({_block.Count} line(s) buffered; finish block or /cancel)";
            return;
        }
        _block.Clear();
        RunLua(candidate, "terminal");
    }

    /// <summary>Run a multi-line script from the EDITOR tab in the console's Lua state.</summary>
    public void SubmitBlock(string code)
    {
        code = (code ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        if (code.Length > MaxEditorChars) code = code[..MaxEditorChars];
        if (code.Length == 0) return;
        Flush();
        _block.Clear();
        int lines = code.Count(c => c == '\n') + 1;
        Out($"> [editor: {lines} line{(lines == 1 ? "" : "s")}]", Ink.Prompt);
        RunLua(code, "editor");
    }

    static bool IsIncompleteLua(string err) =>
        err.Contains("<eof>", StringComparison.OrdinalIgnoreCase)
        || err.Contains("unfinished", StringComparison.OrdinalIgnoreCase);

    void Command(string input)
    {
        var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string cmd = parts[0].ToLowerInvariant(), arg = parts.Length > 1 ? parts[1].Trim() : "";
        switch (cmd)
        {
            case "/help": Help(); break;
            case "/battle":
                if (OpenBattle is null) { Out("no battle window here", Ink.Error); break; }
                OpenBattle(arg.Length == 0 ? "hungry" : arg.ToLowerInvariant());
                Out("A turn-based battle opens (" + (arg.Length == 0 ? "hungry" : arg) + "): choose each move in its window.", Ink.Dim);
                break;
            case "/crawlers":
                if (OpenCrawlers is null) { Out("no crawlers window here", Ink.Error); break; }
                OpenCrawlers();
                Out("Cat Crawlers table opens: click FISH, ROLL BACK, TAKE, RIDE or DEAL.", Ink.Dim);
                break;
            case "/clear": _lines.Clear(); _queue.Clear(); _typing = ""; _shown = 0; Changes++; Status = "cleared"; break;
            case "/scripts": Scripts(); break;
            case "/run": RunScript(arg); break;
            case "/reload": if (LastScript is null) Out("nothing run yet: /run <script> first", Ink.Error); else RunScript(LastScript); break;
            case "/config": Config(arg); break;
            case "/reset": _block.Clear(); _host.ResetLua(); Out("Lua state reset.", Ink.Dim); Status = "reset"; break;
            case "/cancel": _block.Clear(); Out("multi-line buffer is empty.", Ink.Dim); Status = "ready"; break;
            case "/packs": ListPacks(); break;
            case "/start": PackOp(arg, "start"); break;
            case "/stop": PackOp(arg, "stop"); break;
            case "/restart": PackOp(arg, "restart"); break;
            case "/allow": PackPermOp(arg, allow: true); break;
            case "/revoke": PackPermOp(arg, allow: false); break;
            case "/kl": KenshiLuaOp(arg); break;
            default:
                if (Packs is not null && Packs.TryCommand(cmd[1..], arg, out string? pErr, out long pIns))
                {
                    LastInstructions = pIns;
                    if (pErr != null) { Out(pErr, Ink.Error); Status = "error"; }
                    else Status = $"ok, {pIns.ToString("N0", CultureInfo.InvariantCulture)} instructions";
                    break;
                }
                Out($"unknown command {cmd}: try /help", Ink.Error);
                break;
        }
    }

    void Help()
    {
        Out("Commands", Ink.Accent);
        Out("  /help              this list", Ink.Text);
        Out("  /run <script>      run scripts/<script>.lua (read again each time: edit it while the game runs)", Ink.Text);
        Out("  /reload            run the last script again", Ink.Text);
        Out("  /scripts           the scripts you can run", Ink.Text);
        Out("  /config [words]    the settings, filtered by words; /config set <key> <value>", Ink.Text);
        Out("  /reset             a fresh Lua state", Ink.Text);
        Out("  /battle [fight]    animated FF-style turn-based battle: hungry, dust, beak", Ink.Text);
        Out("  /crawlers          clickable Cat Crawlers bar game window (scripts/crawlers.lua)", Ink.Text);
        Out("  /packs             list Lua packs; /start, /stop, /restart, /allow, /revoke <pack>", Ink.Text);
        Out(KenshiLuaRetired
            ? "  /kl [list|stop|revoke]  the old KenshiLua layer (retired: Meitou 0.24.0+ runs KenshiLua mods itself)"
            : "  /kl [list|allow|start|stop|revoke]  KenshiLua mods (scripts/init/*.lua) on Meitou before 0.24.0", Ink.Text);
        if (Packs is not null)
        {
            var custom = Packs.RegisteredCommands();
            if (custom.Count > 0)
                Out("  pack commands:     " + string.Join("  ", custom.Select(c => "/" + c.Name)), Ink.Dim);
        }
        Out("  /clear             clear the screen", Ink.Text);
        Out("Anything else is Lua: print(world.hour()), print(selected.name()), 2^10", Ink.Dim);
    }

    void ListPacks()
    {
        if (Packs is null) { Out("packs are not enabled here", Ink.Dim); return; }
        Packs.Discover();
        var list = Packs.List();
        if (list.Count == 0) { Out("no Lua packs in packs/: add packs/<id>/pack.json", Ink.Dim); return; }
        Out($"{list.Count} Lua pack(s):", Ink.Accent);
        foreach (var (m, run, err, cmds) in list)
        {
            string st = run ? "running" : err is not null ? "error" : "stopped";
            string c = cmds.Count > 0 ? " [" + string.Join(", ", cmds.Select(x => "/" + x)) + "]" : "";
            var miss = Packs.MissingPermissions(m);
            string p = miss.Count > 0 ? $" [needs /allow: {string.Join(", ", miss)}]" : "";
            Out($"  {m.Id,-18} {st,-8} v{m.Version} ({m.Source}) by {m.Author}{c}{p}", Ink.Text);
        }
    }

    void PackOp(string arg, string op)
    {
        if (Packs is null) { Out("packs are not enabled here", Ink.Error); return; }
        if (arg.Length == 0) { Out($"usage: /{op} <pack>  (see /packs)", Ink.Error); return; }
        var (ok, msg) = op switch { "start" => Packs.Start(arg), "stop" => Packs.Stop(arg), _ => Packs.Restart(arg) };
        Out(msg, ok ? Ink.Result : Ink.Error);
        Status = ok ? op : "error";
    }

    void PackPermOp(string arg, bool allow)
    {
        if (Packs is null) { Out("packs are not enabled here", Ink.Error); return; }
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) { Out($"usage: /{(allow ? "allow" : "revoke")} <pack> [perm|all]", Ink.Error); return; }
        string id = parts[0], cap = parts.Length > 1 ? parts[1] : "all";
        var (ok, msg) = allow ? Packs.Allow(id, cap) : Packs.Revoke(id, cap);
        Out(msg, ok ? Ink.Result : Ink.Error);
        Status = ok ? (allow ? "allowed" : "revoked") : "error";
    }

    void KenshiLuaOp(string arg)
    {
        if (KenshiLua is null) { Out("KenshiLua compat host is not enabled here", Ink.Dim); return; }
        var parts = arg.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string sub = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";
        if (KenshiLuaRetired && sub is "start" or "allow")
        {
            Out("retired here: this Meitou (0.24.0+) runs KenshiLua mods itself, so Moonscript's old layer stays off (no mod runs twice)", Ink.Dim);
            return;
        }
        string target = parts.Length > 1 ? parts[1].Trim() : "";
        if (sub == "list")
        {
            KenshiLua.Discover();
            var list = KenshiLua.List();
            if (list.Count == 0) { Out("no KenshiLua mods found (<Mod>/scripts/init/*.lua)", Ink.Dim); return; }
            Out($"{list.Count} KenshiLua compat mod(s):", Ink.Accent);
            foreach (var (info, approved, running, err) in list)
            {
                string st = running ? "running" : err is not null ? "error" : approved ? "stopped" : "needs /kl allow";
                Out($"  {info.ModId,-22} {st,-15} ({info.InitScripts.Count} init script(s))", Ink.Text);
            }
            return;
        }
        if (target.Length == 0) { Out($"usage: /kl {sub} <mod>  (see /kl list)", Ink.Error); return; }
        var (ok, msg) = sub switch
        {
            "allow" => KenshiLua.Allow(target),
            "revoke" => KenshiLua.Revoke(target),
            "start" => KenshiLua.Start(target),
            "stop" => KenshiLua.Stop(target),
            _ => (false, "usage: /kl [list|allow|start|stop|revoke] <mod>"),
        };
        Out(msg, ok ? Ink.Result : Ink.Error);
        Status = ok ? sub : "error";
    }

    void Scripts()
    {
        var list = _host.Scripts();
        if (list.Count == 0) { Out("no scripts: put .lua files in the mod's scripts folder", Ink.Dim); return; }
        Out($"{list.Count} script(s):", Ink.Accent);
        foreach (var s in list) Out("  " + s, Ink.Text);
    }

    void RunScript(string name)
    {
        if (name.Length == 0) { Out("usage: /run <script>  (see /scripts)", Ink.Error); return; }
        var (text, error) = _host.ReadScript(name);
        if (text is null) { Out(error ?? "cannot read it", Ink.Error); return; }
        LastScript = name;
        RunLua(text, name);
    }

    void RunLua(string code, string chunk)
    {
        // an expression typed alone prints its value, as Lua's own prompt does; only a line that compiles as "return <line>" is taken as
        // one (checked by compiling, never by running: a line runs exactly once)
        bool expression = chunk == "terminal" && _host.CompileError("return " + code) is null;
        var (result, error, instructions) = _host.Run(expression ? "return " + code : code, chunk);
        LastInstructions = instructions;
        if (error != null) { Out(error, Ink.Error); Status = "error"; return; }
        if (result != null) Out(result, Ink.Result);
        Status = $"ok, {instructions.ToString("N0", CultureInfo.InvariantCulture)} instructions";
    }

    void Config(string arg)
    {
        var entries = _host.Config();
        if (arg.StartsWith("set ", StringComparison.OrdinalIgnoreCase))
        {
            var kv = arg[4..].Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var e = kv.Length == 2 ? entries.FirstOrDefault(x => x.Key.Equals(kv[0], StringComparison.OrdinalIgnoreCase)) : null;
            if (e is null) { Out("usage: /config set <key> <value>  (keys: /config)", Ink.Error); return; }
            string? err = e.Set(kv[1].Trim());
            if (err != null) Out($"{e.Key}: {err}", Ink.Error);
            else Out($"{e.Key} = {e.Get()}", Ink.Result);
            return;
        }
        var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var shown = entries.Where(e => words.All(w => e.Key.Contains(w, StringComparison.OrdinalIgnoreCase)
                                                    || e.Description.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        Out(words.Length == 0 ? $"{shown.Count} settings (filter: /config <words>)" : $"{shown.Count} of {entries.Count} settings match '{arg}'", Ink.Accent);
        foreach (var e in shown) Out($"  {e.Key,-18} {e.Get(),-8} {e.Description}", Ink.Text);
    }
}
