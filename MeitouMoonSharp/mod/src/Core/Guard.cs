// MeitouMoonscript: the instruction / stack / allocation guard. Author: PZA. Licence: GPLv3 or later.
//
// Why a debugger: MoonSharp's AutoYieldCounter counts only the coroutine the host resumes and cannot yield across a C# boundary, so a
// script-made coroutine, a table.sort comparator or a gsub callback ran forever (tools/euler, attacks-naive.txt, 2026-11-22). The VM calls
// IDebugger.IsPauseRequested() before EVERY instruction of EVERY coroutine of a Script (child processors share the parent's DebugContext;
// Processor_InstructionLoop.cs / Processor.cs, MoonSharp 7b2e3e4), so counting there is exact and has no escape. A stop is thrown as an
// exception that is not an InterpreterException: the VM's catch only handles those, and pcall / xpcall catch only ScriptRuntimeException,
// so a stop unwinds the whole call (claims C1-C3, tools/euler/CLAIMS.md). The Script is never run again after a stop (claim C8).
using System.Runtime.CompilerServices;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Debugging;

namespace PZA.Moonscript.Core;

/// <summary>Why a script was stopped. Not an InterpreterException on purpose (see the file header).</summary>
public sealed class GuardStop(string reason, string detail) : Exception(reason + ": " + detail)
{
    public string Reason { get; } = reason;
    public string Detail { get; } = detail;
}

public sealed class ScriptLimits
{
    /// <summary>Bc: VM instructions per call.</summary>
    public long InstructionsPerCall = 200_000;
    /// <summary>Bt: VM instructions per world tick, summed over the script's calls.</summary>
    public long InstructionsPerTick = 1_000_000;
    /// <summary>Mc: estimated bytes the guarded library functions may create in one call, charged before they allocate
    /// (2 per character, Guard.ValueCost per value, LiveSize.CoroutineCharge per coroutine).</summary>
    public long BytesPerCall = 32_000_000;
    /// <summary>C# stack a call may use below the frame that started it (the Euler pass crashed a process with gsub nested in gsub
    /// while TryEnsureSufficientExecutionStack still said yes: its margin is smaller than MoonSharp's nested frames).</summary>
    public long StackBytes = 4 * 1024 * 1024;   // of LuaThread's 16 MB: unwinding from depth d needs several d more
    /// <summary>Nested guarded C# functions (each C#-to-Lua re-entry passes one: gsub, sort, tostring, resume). The same count on every
    /// machine, so a deep script faults at the same point on x64 and arm64 (claim C6); StackBytes is the backstop.</summary>
    public int ClrDepth = 24;
    /// <summary>Ml: the live-size estimate (LiveSize) allowed after a call.</summary>
    public long LiveBytes = 16_000_000;
    /// <summary>Safety net only (not deterministic near the cap): bytes the call's thread may allocate, checked every 16 instructions.</summary>
    public long AllocBytesPerCall = 64_000_000;

    public ScriptLimits Clone() => (ScriptLimits)MemberwiseClone();
}

public sealed class Guard : IDebugger
{
    readonly ScriptLimits _l;
    long _call, _tick, _chars, _allocStart, _stackStart;
    int _depth;
    GuardStop? _stopped;   // sticky for the rest of the call (fuzz guard.budget, 2026-11-22: a stop inside a table.sort comparator was
                           // counted one instruction late while .NET's sort wrapped and MoonSharp rethrew it); never counted again
    bool _inCall;

    public Guard(ScriptLimits limits) => _l = limits;

    public long CallInstructions => _call;
    public long TickInstructions => _tick;
    public long TotalInstructions { get; private set; }
    public long CallBytes => _chars;

    public void BeginTick() => _tick = 0;

    /// <summary>A value in a MoonSharp table or argument list (measured: string.byte of 1e6 chars into a table, 239 MB).</summary>
    public const double ValueCost = 200;

    static unsafe long StackPointer() { byte b; return (long)&b; }

    public void BeginCall()
    {
        _call = 0; _chars = 0; _inCall = true; _stackStart = StackPointer(); _depth = 0; _stopped = null;
        _allocStart = GC.GetAllocatedBytesForCurrentThread();
    }

    public void EndCall() => _inCall = false;

    /// <summary>Called by the VM before every instruction (see the file header).</summary>
    public bool IsPauseRequested()
    {
        // Lua outside a host call would run with no budget and a stale stack base: never (the Euler gsub check found it in a test)
        if (!_inCall) throw new GuardStop("outside", "Lua ran outside a host call");
        if (_stopped != null) throw _stopped;
        long c = ++_call, t = ++_tick;
        TotalInstructions++;
        if (c > _l.InstructionsPerCall) throw Stop("budget", $"more than {_l.InstructionsPerCall} instructions in one call");
        if (t > _l.InstructionsPerTick) throw Stop("tick-budget", $"more than {_l.InstructionsPerTick} instructions in one tick");
        CheckStack();
        if ((c & 15) == 0) CheckAlloc();
        return false;
    }

    /// <summary>Every guarded C# callback runs through here: a C#-only recursion (tostring as its own __tostring) still meets a check,
    /// and the nesting is counted (ClrDepth).</summary>
    public DynValue Clr(Func<DynValue> body)
    {
        if (!_inCall) return body();
        if (_stopped != null) throw _stopped;
        if (++_depth > _l.ClrDepth) { _depth--; throw Stop("stack", $"more than {_l.ClrDepth} nested library calls"); }
        try
        {
            CheckStack();
            CheckAlloc();
            return body();
        }
        finally { _depth--; }
    }

    /// <summary>Charge n estimated bytes a library function is about to create. Throws before the allocation.</summary>
    public void Charge(double n, string what)
    {
        if (!_inCall) return;
        if (!(n >= 0)) n = double.MaxValue;   // NaN or negative sizes are refused by the caller's own checks; never undercharge
        if (_chars + n > _l.BytesPerCall) throw Stop("memory", $"{what} would create {n:0} bytes (limit {_l.BytesPerCall} per call)");
        _chars += (long)n;
    }

    GuardStop Stop(string reason, string detail) => _stopped ??= new GuardStop(reason, detail);

    void CheckStack()
    {
        long used = _stackStart - StackPointer();   // the stack grows down on every platform .NET runs Meitou on
        if (used > _l.StackBytes || !RuntimeHelpers.TryEnsureSufficientExecutionStack())
            throw Stop("stack", $"too deep ({used / 1024} KB of C# stack)");
    }

    void CheckAlloc()
    {
        long used = GC.GetAllocatedBytesForCurrentThread() - _allocStart;
        if (used > _l.AllocBytesPerCall) throw Stop("memory-hard", $"{used} bytes allocated in one call");
    }

    // ---- the rest of IDebugger: a debugger that never pauses ----
    public DebuggerCaps GetDebuggerCaps() => 0;
    public void SetDebugService(DebugService debugService) { }
    public void SetSourceCode(SourceCode sourceCode) { }
    public void SetByteCode(string[] byteCode) { }
    public bool SignalRuntimeException(ScriptRuntimeException ex) => false;
    public DebuggerAction GetAction(int ip, SourceRef sourceref) => new() { Action = DebuggerAction.ActionType.Run };
    public void SignalExecutionEnded() { }
    public void Update(WatchType watchType, IEnumerable<WatchItem> items, int stackFrameIndex) { }
    public List<DynamicExpression> GetWatchItems() => [];
    public void RefreshBreakpoints(IEnumerable<SourceRef> refs) { }
}
