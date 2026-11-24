// MeitouMoonscript: clickable Cat Crawlers window session, game-free. Author: PZA. Licence: GPLv3 or later.
// Runs scripts/crawlers.lua in its own UI-side LuaBox (practice cats only, never touches real world money) so players can click
// FISH / ROLL BACK / TAKE / RIDE / DEAL instead of typing Lua function calls in the terminal.
using MoonSharp.Interpreter;
using PZA.Moonscript.Core;

namespace PZA.Moonscript.Repl;

public sealed class CrawlersSession
{
    readonly string _script;
    readonly Func<string?> _selected;
    readonly Func<long?> _cats;
    readonly Action<string> _log;
    readonly List<string> _lines = [];
    LuaBox? _box;
    ulong _seed = 101;

    public string? Note { get; private set; }
    public int Phase { get; private set; }          // 0 Idle, 1 Choose, 2 Licked, 3 Dry
    public int Face { get; private set; }
    public int Drawn { get; private set; }
    public int Stake { get; private set; }
    public int Pot { get; private set; }
    public int Chain { get; private set; }
    public int Purse { get; private set; } = 1000;
    public int Net { get; private set; }
    public int Total { get; private set; } = 28;
    public int[] Left { get; } = [4, 4, 4, 4, 4, 4, 4];
    public IReadOnlyList<string> Lines => _lines;

    public static readonly string[] Names =
        ["Wolf Tail", "Buxom Red", "Chubby Nine", "Stoneroller", "Hungry Gutter", "Arrow in the Neck", "Plank"];

    public CrawlersSession(string scriptPath, Func<string?> selected, Func<long?> cats, Action<string> log)
    {
        _script = scriptPath; _selected = selected; _cats = cats; _log = log;
    }

    public bool CanDeal => Phase is 0 or 3;
    public bool CanCall => Phase == 1 && Face is >= 2 and <= 6;
    public bool CanTake => Phase == 2;
    public bool CanRide => Phase == 2 && Drawn is > 1 and < 7 && Pot < 6000;

    public int PaysFish => CanCall ? (int)Math.Floor(Pot * 6.0 / (7 - Face)) : 0;
    public int PaysRoll => CanCall ? (int)Math.Floor(Pot * 6.0 / (Face - 1)) : 0;

    public void Open()
    {
        if (_box is not null && !_box.Faulted) { Sync(); return; }
        Restart();
    }

    public void Restart()
    {
        _lines.Clear();
        Note = null;
        string text;
        try { text = File.ReadAllText(_script); }
        catch (Exception e) { Note = "can't read crawlers.lua: " + e.GetType().Name; return; }

        _box = new LuaBox("crawlers", new ScriptLimits(), _seed++, AddLine, _log) { Forgiving = true };
        var box = _box;
        box.Expose("world", "cats", _ => _cats() is long c ? DynValue.NewNumber(c) : DynValue.Nil);
        box.Expose("selected", "name", _ => _selected() is { Length: > 0 } n ? DynValue.NewString(n) : DynValue.Nil);
        box.Load("crawlers", text);
        Sync();
    }

    public void Action(string fn, int arg = 0)
    {
        if (_box is null || _box.Faulted) { Restart(); return; }
        if (fn == "deal") _box.Call("deal", DynValue.NewNumber(arg));
        else _box.Call(fn);
        Sync();
    }

    void AddLine(string s)
    {
        _lines.Add(s);
        if (_lines.Count > 24) _lines.RemoveAt(0);
    }

    void Sync()
    {
        if (_box is null) return;
        if (_box.Faulted) { Note = $"crawlers stopped: {_box.Fault}: {_box.FaultDetail}"; return; }
        if (_box.LastError is not null) { Note = _box.LastError; return; }
        Note = null;
        var st = ScriptValue.FromLua(_box.Eval("return _G._crawl"));
        if (st.Kind != ScriptKind.Table) return;
        Phase = (int)st["phase"].Number;
        Face = (int)st["face"].Number;
        Drawn = (int)st["drawn"].Number;
        Stake = (int)st["stake"].Number;
        Pot = (int)st["pot"].Number;
        Chain = (int)st["chain"].Number;
        Purse = (int)st["purse"].Number;
        Net = (int)st["net"].Number;
        Total = (int)st["total"].Number;
        if (st["left"].Table is not null)
            for (int i = 0; i < 7; i++) Left[i] = (int)st["left"][i + 1].Number;
    }
}
