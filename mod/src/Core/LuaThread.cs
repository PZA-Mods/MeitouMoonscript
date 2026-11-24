// MeitouMoonscript: the one thread every script runs on. Author: PZA. Licence: GPLv3 or later.
//
// Why (tools/euler/stack-depth.txt, 2026-11-22): a stop thrown deep inside nested MoonSharp frames needs several times that depth again to
// unwind under .NET 11's exception handling; nested table.sort and nested coroutines overflowed a 256 KB thread at 24 levels, gsub at 48.
// A stack overflow kills the game. The caller's thread and how deep the game already is are unknown, so every call is handed to this
// thread: a fixed 16 MB stack, the same base for every call (the stack cap in Guard is then the same each run), one call at a time.
// The caller waits; calls stay in the caller's order, so nothing about determinism changes. A host function a script calls runs here
// too: hosts pass the world facts in (a snapshot), they never touch the World from a script call.
namespace PZA.Moonscript.Core;

public sealed class LuaThread
{
    public const int StackSize = 16 * 1024 * 1024;
    public static readonly LuaThread Shared = new();

    readonly Thread _t;
    readonly SemaphoreSlim _go = new(0), _done = new(0);
    Action? _work;
    Exception? _error;
    readonly object _gate = new();

    LuaThread()
    {
        _t = new Thread(Loop, StackSize) { IsBackground = true, Name = "PZA Lua" };
        _t.Start();
    }

    public bool OnThread => Thread.CurrentThread == _t;

    public T Invoke<T>(Func<T> f)
    {
        if (OnThread) return f();   // a host function calling back into a box: already here
        T result = default!;
        lock (_gate)   // one caller at a time; the order is the callers' own
        {
            _error = null;
            _work = () => result = f();
            _go.Release();
            _done.Wait();
            if (_error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_error).Throw();
        }
        return result;
    }

    void Loop()
    {
        while (true)
        {
            _go.Wait();
            try { _work!(); } catch (Exception e) { _error = e; }
            _work = null;
            _done.Release();
        }
    }
}
