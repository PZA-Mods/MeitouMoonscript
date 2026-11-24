// MeitouMoonscript: a deterministic estimate of what a script keeps alive. Author: PZA. Licence: GPLv3 or later.
//
// Claim C5 (live part): after every call, Size(script) <= Ml or the script is faulted "memory". The estimate is structural (the same
// on every machine and run), never the runtime's byte counters:
//   string: 24 + 2 * length          table: 64 + 48 per entry + its metatable     closure: 64 + 16 per upvalue + the upvalues' values
//   coroutine: CoroutineCost (256 KB, measured) + 16 per value-stack slot + its frames' locals and closure scopes (read from MoonSharp's internals by
//   reflection, pinned to the MoonSharp build this host ships; Euler attack "hide.coroutine" plants data there)
// Shared values are counted once. Roots: the globals, the registry, the string metatable.
// The walk also clears every visited table's dead entries (StateCodec's header): it runs after every call, so it is the one place that
// makes the state canonical between calls.
using System.Reflection;
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public static class LiveSize
{
    /// <summary>A coroutine's own stacks: measured 2026-11-22 (coroutine.nested: 65 MB for about 260 nested coroutines).</summary>
    public const double CoroutineCost = 262_144;
    /// <summary>What coroutine.create / wrap charge against the call's bytes (a dead coroutine's processor is recycled).</summary>
    public const double CoroutineCharge = 65_536;

    static readonly FieldInfo? CoProcessor = typeof(Coroutine).GetField("m_Processor", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly Type? ProcessorType = CoProcessor?.FieldType;
    static readonly FieldInfo? ValueStack = ProcessorType?.GetField("m_ValueStack", BindingFlags.Instance | BindingFlags.NonPublic);
    static readonly FieldInfo? ExecStack = ProcessorType?.GetField("m_ExecutionStack", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>False when this MoonSharp build's internals differ: the host then refuses coroutines (SafeLib) rather than miss data.</summary>
    public static bool CanSeeCoroutines => ValueStack != null && ExecStack != null;

    public static long Of(Script s, long cap)
    {
        var w = new Walker(cap);
        w.Table(s.Globals);
        w.Table(s.Registry);
        var sm = s.GetTypeMetatable(DataType.String);
        if (sm != null) w.Table(sm);
        return w.Bytes;
    }

    /// <summary>Remove a table's dead entries (nil values). CollectDeadKeys alone leaves some (Euler C7.codec, seeds 310, 440, ...).</summary>
    public static void Canon(Table t)
    {
        t.CollectDeadKeys();
        List<DynValue>? dead = null;
        foreach (var p in t.Pairs) if (p.Value.IsNil()) (dead ??= []).Add(p.Key);
        if (dead != null) foreach (var k in dead) t.Remove(k);
    }

    sealed class Walker(long cap)
    {
        public long Bytes;
        readonly HashSet<object> _seen = new(ReferenceEqualityComparer.Instance);
        readonly Stack<DynValue> _todo = new();

        void Add(double n) { Bytes += (long)n; }

        public void Table(Table t) { if (_seen.Add(t)) { TableBody(t); Drain(); } }

        void TableBody(Table t)
        {
            Canon(t);   // the canonical state between calls (StateCodec's header, claim C7)
            Add(64);
            foreach (var p in t.Pairs) { Add(48); _todo.Push(p.Key); _todo.Push(p.Value); }
            if (t.MetaTable != null && _seen.Add(t.MetaTable)) TableBody(t.MetaTable);
        }

        void Drain()
        {
            while (_todo.Count > 0 && Bytes <= cap)
            {
                var v = _todo.Pop();
                if (v == null) continue;
                switch (v.Type)
                {
                    case DataType.String: Add(24 + 2.0 * v.String.Length); break;
                    case DataType.Table: if (_seen.Add(v.Table)) TableBody(v.Table); break;
                    case DataType.Function:
                        var f = v.Function;
                        if (!_seen.Add(f)) break;
                        int n = f.GetUpvaluesCount();
                        Add(64 + 16.0 * n);
                        for (int i = 0; i < n; i++) _todo.Push(f.GetUpvalue(i));
                        break;
                    case DataType.Thread:
                        var co = v.Coroutine;
                        if (!_seen.Add(co)) break;
                        Add(CoroutineCost);
                        Coroutine(co);
                        break;
                    case DataType.Tuple:
                        foreach (var x in v.Tuple) _todo.Push(x);
                        break;
                    case DataType.ClrFunction:
                        // coroutine.wrap's function holds its coroutine here (Euler C5.live: 20 MB hidden in a wrapped coroutine)
                        Add(64);
                        if (v.Callback.AdditionalData is Coroutine wc) _todo.Push(DynValue.NewCoroutine(wc));
                        else if (v.Callback.AdditionalData is DynValue wd) _todo.Push(wd);
                        break;
                    default: Add(16); break;
                }
            }
        }

        void Coroutine(Coroutine co)
        {
            if (!CanSeeCoroutines) return;
            var p = CoProcessor!.GetValue(co);
            if (p == null) return;
            foreach (var x in Stack(ValueStack!.GetValue(p))) { Add(16); if (x is DynValue d) _todo.Push(d); }
            foreach (var frame in Stack(ExecStack!.GetValue(p)))
            {
                if (frame == null) continue;
                var ft = frame.GetType();
                if (ft.GetField("LocalScope")?.GetValue(frame) is DynValue[] locals)
                    foreach (var l in locals) { Add(16); if (l != null) _todo.Push(l); }
                if (ft.GetField("ClosureScope")?.GetValue(frame) is List<DynValue> scope)
                    foreach (var l in scope) { Add(16); if (l != null) _todo.Push(l); }
            }
        }

        static IEnumerable<object?> Stack(object? fastStack)
        {
            if (fastStack == null) yield break;
            var t = fastStack.GetType();
            var storage = t.GetField("m_Storage", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fastStack) as Array;
            var head = t.GetField("m_HeadIdx", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(fastStack) as int?;
            if (storage == null || head == null) yield break;
            for (int i = 0; i < head.Value && i < storage.Length; i++) yield return storage.GetValue(i);
        }
    }
}
