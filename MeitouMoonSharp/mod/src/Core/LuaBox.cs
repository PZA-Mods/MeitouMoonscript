// MeitouMoonscript: one sandboxed script. Author: PZA. Licence: GPLv3 or later.
//
// The contract a script lives by (README "Writing a script"):
//   - Everything it must keep goes in the global table `state` (data only: see StateCodec). After a load the host runs the script's
//     text again, puts the saved `state` back and calls on_load() if the script has one. Anything else is rebuilt by the text.
//   - math.random is the world's seeded random (Rng.cs); there is no clock, no files, no os, no load.
//   - A call that runs too long, too deep or too big, or throws, faults the box for that world (claim C8): it never runs Lua again,
//     every later call returns nil, and the fault (reason, tick) is in StateHash.
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public sealed class LuaBox
{
    public string Id { get; }
    public ScriptLimits Limits { get; }
    public string? Fault { get; private set; }
    public string? FaultDetail { get; private set; }
    public long FaultTick { get; private set; } = -1;
    public long Tick { get; private set; }
    public long Calls { get; private set; }
    public int DroppedOnSave { get; private set; }
    public long TotalInstructions => _guard.TotalInstructions;
    public long LastCallInstructions => _guard.CallInstructions;
    public long LastCallBytes => _guard.CallBytes;

    readonly Guard _guard;
    readonly ScriptRng _rng;
    readonly Action<string> _print;
    readonly Action<string> _log;
    readonly Script _s;
    readonly List<(string table, string name)> _host = [];

    public LuaBox(string id, ScriptLimits limits, ulong seed, Action<string> print, Action<string> log)
    {
        Id = id; Limits = limits.Clone(); _print = print; _log = log;
        _guard = new Guard(Limits);
        _rng = new ScriptRng(seed);
        _s = LuaThread.Shared.Invoke(() =>
        {
            var s = SafeLib.Build(_guard, _rng, print);
            StdLib.Install(s);
            s.AttachDebugger(_guard);
            return s;
        });
    }

    public bool Faulted => Fault != null;

    /// <summary>A terminal's box: a Lua error or a syntax error comes back in LastError and the box keeps running (MoonSharp unwinds its own
    /// errors cleanly). A guard stop (budget, memory, stack) still faults it: it leaves MoonSharp's processor mid-call, never to be reused.</summary>
    public bool Forgiving { get; init; }

    /// <summary>The last call's Lua error in a Forgiving box, else null.</summary>
    public string? LastError { get; private set; }

    /// <summary>Null when the text compiles, else the syntax error. Compiling runs no Lua.</summary>
    public string? CompileError(string code) => LuaThread.Shared.Invoke(() =>
    {
        try { _s.LoadString(code, null, "check"); return (string?)null; }
        catch (SyntaxErrorException e) { return e.DecoratedMessage ?? e.Message; }
        catch (InterpreterException e) { return e.DecoratedMessage ?? e.Message; }
    });
    public Script RawScript => _s;   // tests only; the host never hands it out

    /// <summary>Add a host function as table.name (table "" = a global). Called before Load; wrapped like the library's.</summary>
    public void Expose(string table, string name, Func<CallbackArguments, DynValue> f)
    {
        Table t = _s.Globals;
        if (table != "")
        {
            var v = _s.Globals.Get(table);
            if (v.Type != DataType.Table) { t = new Table(_s); _s.Globals[table] = t; } else t = v.Table;
        }
        t[name] = DynValue.NewCallback(SafeLib.Callback(name, (c, a) => _guard.Clr(() => f(a))));
        _host.Add((table, name));
    }

    public IReadOnlyList<(string table, string name)> HostFunctions => _host;

    public void BeginTick(long tick) { Tick = tick; _guard.BeginTick(); }

    /// <summary>Compile and run the script's text (its top level is one call under the budget).</summary>
    public bool Load(string chunkName, string code)
    {
        if (Faulted) return false;
        return Run(() => _s.DoString(code, null, chunkName)) != null;
    }

    /// <summary>Call a global function. Nil when faulted or when the script has no such function (not a fault).</summary>
    public DynValue Call(string fn, params DynValue[] args)
    {
        if (Faulted) return DynValue.Nil;
        var f = _s.Globals.RawGet(fn);
        if (f == null || f.Type != DataType.Function) return DynValue.Nil;
        return Run(() => _s.Call(f, args)) ?? DynValue.Nil;
    }

    /// <summary>Run text typed at the terminal: compiled and run as one call.</summary>
    public DynValue Eval(string code)
    {
        if (Faulted) return DynValue.Nil;
        return Run(() => _s.DoString(code, null, "terminal")) ?? DynValue.Nil;
    }

    DynValue? Run(Func<DynValue> body) => LuaThread.Shared.Invoke(() => RunHere(body));

    DynValue? RunHere(Func<DynValue> body)
    {
        Calls++;
        LastError = null;
        _guard.BeginCall();
        try
        {
            var r = body();
            _guard.EndCall();
            long live = LiveSize.Of(_s, Limits.LiveBytes);
            if (live > Limits.LiveBytes) { SetFault("memory", $"keeps more than {Limits.LiveBytes} bytes alive"); return null; }
            return r;
        }
        catch (GuardStop g) { SetFault(g.Reason, g.Detail); }
        catch (SyntaxErrorException e) { if (Forgiving) LastError = e.DecoratedMessage ?? e.Message; else SetFault("syntax", e.DecoratedMessage ?? e.Message); }
        catch (ScriptRuntimeException e) { if (Forgiving) LastError = e.DecoratedMessage ?? e.Message; else SetFault("error", e.DecoratedMessage ?? e.Message); }
        catch (InterpreterException e) { if (Forgiving) LastError = e.DecoratedMessage ?? e.Message; else SetFault("error", e.DecoratedMessage ?? e.Message); }
        catch (InsufficientExecutionStackException) { SetFault("stack", "too deep (C# stack)"); }
        catch (OutOfMemoryException) { SetFault("memory-hard", "out of memory"); }
        catch (Exception e) { SetFault("clr", e.GetType().Name + ": " + e.Message); }
        finally { _guard.EndCall(); }
        return null;
    }

    void SetFault(string reason, string detail)
    {
        if (Faulted) return;
        Fault = reason; FaultDetail = detail; FaultTick = Tick;
        _log($"script {Id} switched off for this world at tick {Tick}: {reason}: {detail}");
    }

    /// <summary>The save: rng state, then `state`. Never runs Lua.</summary>
    public byte[] Save()
    {
        var w = new StateCodec.Writer();
        w.U64(_rng.State);
        w.Value(_s.Globals.RawGet("state"));
        DroppedOnSave = w.Dropped;
        return w.ToArray();
    }

    /// <summary>After Load: put a saved `state` and rng back, then on_load(). A malformed save faults the box, never throws.</summary>
    public bool Restore(byte[] blob)
    {
        if (Faulted) return false;
        try
        {
            var r = new StateCodec.Reader(blob, _s);
            ulong rng = r.U64();
            var st = r.Value();
            if (!r.AtEnd) throw new FormatException("bytes after the state");
            _rng.State = rng;
            _s.Globals["state"] = st;
        }
        catch (FormatException e) { SetFault("save", e.Message); return false; }
        Call("on_load");
        return !Faulted;
    }

    /// <summary>Claim C6/C8: the save bytes, the fault and its tick, the instructions run. Never runs Lua.</summary>
    public ulong StateHash()
    {
        ulong h = StateCodec.Fnv(Save());
        h = StateCodec.Fnv(System.Text.Encoding.UTF8.GetBytes($"{Fault}|{FaultTick}|{_guard.TotalInstructions}|{Calls}"), h);
        return h;
    }
}
