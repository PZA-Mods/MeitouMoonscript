// MeitouMoonscript: the sandboxed library a script sees. Author: PZA. Licence: GPLv3 or later.
//
// Claims (tools/euler/CLAIMS.md): C4, the set of C# functions reachable from a script is exactly AllowList; C5, a library call that
// creates text or values is charged before it allocates; C6, nothing reads the clock or an unseeded random. Every C# function is replaced
// in place by a wrapper that calls Guard.EnterClr first (so a C#-only recursion meets a stack check) and keeps its old name.
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public static class SafeLib
{
    /// <summary>MoonSharp modules loaded: no io, os, load*, debug, dynamic, json; collectgarbage and string.dump are removed after.</summary>
    public const CoreModules Modules = CoreModules.Basic | CoreModules.GlobalConsts | CoreModules.TableIterators | CoreModules.Metatables |
                                       CoreModules.String | CoreModules.Table | CoreModules.ErrorHandling | CoreModules.Math |
                                       CoreModules.Coroutine | CoreModules.Bit32;

    /// <summary>Removed from what the modules load: GC control, bytecode dump, platform facts (they differ by machine: claim C6).</summary>
    static readonly string[] RemovedGlobals = ["collectgarbage", "_MOONSHARP", "package"];   // package: aliases of the libraries (Euler C4)
    static readonly string[] RemovedString = ["dump"];

    /// <summary>Every C# function a script may reach, by path (claim C4). Host functions are added under their own table names.</summary>
    public static readonly string[] AllowList =
    [
        "_G.assert", "_G.error", "_G.getmetatable", "_G.ipairs", "_G.next", "_G.pairs", "_G.pcall", "_G.print", "_G.rawequal", "_G.rawget",
        "_G.rawlen", "_G.rawset", "_G.pack", "_G.select", "_G.setmetatable", "_G.tonumber", "_G.tostring", "_G.type", "_G.unpack", "_G.xpcall",
        "bit32.arshift", "bit32.band", "bit32.bnot", "bit32.bor", "bit32.btest", "bit32.bxor", "bit32.extract", "bit32.lrotate", "bit32.lshift",
        "bit32.replace", "bit32.rrotate", "bit32.rshift",
        "coroutine.create", "coroutine.resume", "coroutine.running", "coroutine.status", "coroutine.wrap", "coroutine.yield",
        "math.abs", "math.acos", "math.asin", "math.atan", "math.atan2", "math.ceil", "math.cos", "math.cosh", "math.deg", "math.exp",
        "math.floor", "math.fmod", "math.frexp", "math.ldexp", "math.log", "math.max", "math.min", "math.modf", "math.pow", "math.rad",
        "math.random", "math.randomseed", "math.sin", "math.sinh", "math.sqrt", "math.tan", "math.tanh",
        "string.byte", "string.char", "string.contains", "string.endsWith", "string.find", "string.format", "string.gmatch", "string.gsub",
        "string.len", "string.lower", "string.match", "string.rep", "string.reverse", "string.startsWith", "string.sub", "string.unicode",
        "string.upper",
        "table.concat", "table.insert", "table.pack", "table.remove", "table.sort", "table.unpack",
    ];

    /// <summary>Make a sandboxed Script: modules, removals, guarded replacements, every C# function wrapped.</summary>
    public static Script Build(Guard guard, ScriptRng rng, Action<string> print)
    {
        var s = new Script(Modules);
        s.Options.DebugPrint = _ => { };   // print is replaced below; nothing reaches the console
        var g = s.Globals;
        foreach (var n in RemovedGlobals) g.Remove(n);
        var str = g.Get("string").Table;
        foreach (var n in RemovedString) str.Remove(n);
        var tab = g.Get("table").Table;
        var math = g.Get("math").Table;

        // ---- replacements (exact definitions in Rng.cs and tools/euler/CLAIMS.md) ----
        g["print"] = Fn("print", (c, a) =>
        {
            var parts = new string[a.Count];
            for (int i = 0; i < a.Count; i++) parts[i] = Plain(a[i]);
            guard.Charge(2.0 * parts.Sum(p => (double)p.Length), "print");
            print(string.Join("\t", parts));
            return DynValue.Nil;
        });
        math["random"] = Fn("random", (c, a) =>
        {
            if (a.Count == 0 || (a.Count == 1 && a[0].IsNil())) return DynValue.NewNumber(rng.NextUnit());
            double m = a.AsType(0, "random", DataType.Number, false).Number;
            double lo = 1, hi = Math.Floor(m);
            if (a.Count >= 2 && !a[1].IsNil()) { lo = Math.Floor(m); hi = Math.Floor(a.AsType(1, "random", DataType.Number, false).Number); }
            if (!(lo <= hi)) throw new ScriptRuntimeException("bad argument to 'random' (interval is empty)");
            if (!(hi - lo < ScriptRng.TwoTo53)) throw new ScriptRuntimeException("bad argument to 'random' (interval too large)");
            return DynValue.NewNumber(rng.NextRange(lo, hi));
        });
        math["randomseed"] = Fn("randomseed", (c, a) =>
        {
            double x = a.AsType(0, "randomseed", DataType.Number, false).Number;
            rng.State = ScriptRng.Mix((ulong)BitConverter.DoubleToInt64Bits(x));
            return DynValue.Nil;
        });

        // ---- charged before they allocate (claim C5) ----
        Charge(str, "rep", guard, a =>
        {
            double len = a.Count > 0 && a[0].Type == DataType.String ? a[0].String.Length : 16;
            double n = a.Count > 1 && a[1].Type == DataType.Number ? Math.Floor(a[1].Number) : 0;
            double sep = a.Count > 2 && a[2].Type == DataType.String ? a[2].String.Length : 0;
            return n <= 0 ? 0 : 2 * (len * n + sep * (n - 1));
        });
        Charge(str, "byte", guard, a => Guard.ValueCost * Span(a, 1, 1, 1));
        Charge(str, "unicode", guard, a => Guard.ValueCost * Span(a, 1, 1, 1));
        Charge(str, "char", guard, a => 2.0 * a.Count);
        Charge(str, "upper", guard, a => 2 * StrLen(a, 0));
        Charge(str, "lower", guard, a => 2 * StrLen(a, 0));
        Charge(str, "reverse", guard, a => 2 * StrLen(a, 0));
        Charge(str, "format", guard, a =>
        {   // measured: %q allocates about 100 bytes per character of its argument, a plain format about 4 per output character
            string fmt = a.Count > 0 && a[0].Type == DataType.String ? a[0].String : "";
            double perChar = fmt.Contains("%q") ? 120 : 6;
            double n = 6 * fmt.Length;
            for (int i = 1; i < a.Count; i++) n += perChar * (a[i].Type == DataType.String ? a[i].String.Length : 24) + 200;
            return n;
        });
        Charge(tab, "unpack", guard, a => 2 * Guard.ValueCost * Span(a, 1, 1, -2));
        Charge(g, "unpack", guard, a => 2 * Guard.ValueCost * Span(a, 1, 1, -2));
        Charge(tab, "pack", guard, a => Guard.ValueCost * a.Count);
        Charge(tab, "concat", guard, a => 8 * ConcatSize(a, guard));   // measured 18 bytes per character with the factor 2
        GuardGsub(str, guard);
        Charge(g.Get("coroutine").Table, "create", guard, _ => LiveSize.CoroutineCharge);
        Charge(g.Get("coroutine").Table, "wrap", guard, _ => LiveSize.CoroutineCharge);

        // ---- every C# function: Guard.EnterClr first ----
        WrapAll(s, guard);
        return s;
    }

    public static CallbackFunction Callback(string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> f) => new(f, name);
    static DynValue Fn(string name, Func<ScriptExecutionContext, CallbackArguments, DynValue> f) => DynValue.NewCallback(Callback(name, f));

    /// <summary>A plain text form of a value that never runs a metamethod (no __tostring: that would be Lua outside the guard's call).</summary>
    public static string Plain(DynValue v) => v.Type switch
    {
        DataType.Nil or DataType.Void => "nil",
        DataType.Boolean => v.Boolean ? "true" : "false",
        DataType.Number => NumberText(v.Number),
        DataType.String => v.String,
        DataType.Table => "table",
        DataType.Function or DataType.ClrFunction => "function",
        DataType.Thread => "thread",
        _ => v.Type.ToString().ToLowerInvariant(),
    };

    /// <summary>Lua 5.2's "%.14g", culture-free (claim C6: a German Windows must print the same).</summary>
    public static string NumberText(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsPositiveInfinity(d)) return "inf";
        if (double.IsNegativeInfinity(d)) return "-inf";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture);
        return d.ToString("G14", System.Globalization.CultureInfo.InvariantCulture);
    }

    static double StrLen(CallbackArguments a, int i) => a.Count > i && a[i].Type == DataType.String ? a[i].String.Length : 24;

    /// <summary>How many values a (s|t, i, j) range makes. jDefault -1 = the string's length, -2 = the table's length.</summary>
    static double Span(CallbackArguments a, int iArg, double iDefault, double jDefault)
    {
        double len = a.Count > 0 ? a[0].Type == DataType.String ? a[0].String.Length : a[0].Type == DataType.Table ? a[0].Table.Length : 0 : 0;
        double i = a.Count > iArg && a[iArg].Type == DataType.Number ? Math.Floor(a[iArg].Number) : iDefault;
        double j = a.Count > iArg + 1 && a[iArg + 1].Type == DataType.Number ? Math.Floor(a[iArg + 1].Number)
                 : jDefault == -1 ? len : jDefault == -2 ? len : i;
        if (a.Count > 0 && a[0].Type == DataType.String)
        {   // string positions clamp to the string (string.byte's own rule), so a huge j costs nothing extra
            if (i < 0) i = Math.Max(len + i + 1, 1);
            if (j < 0) j = len + j + 1;
            j = Math.Min(j, len); i = Math.Max(i, 1);
        }
        return j >= i ? j - i + 1 : 0;
    }

    static double ConcatSize(CallbackArguments a, Guard guard)
    {
        if (a.Count == 0 || a[0].Type != DataType.Table) return 0;
        var t = a[0].Table;
        double sep = a.Count > 1 && a[1].Type == DataType.String ? a[1].String.Length : 0;
        double i = a.Count > 2 && a[2].Type == DataType.Number ? Math.Floor(a[2].Number) : 1;
        double j = a.Count > 3 && a[3].Type == DataType.Number ? Math.Floor(a[3].Number) : t.Length;
        if (j < i) return 0;
        guard.Charge(16 * (j - i + 1), "table.concat range");   // the walk below is as long as the range: charge it first
        double n = sep * (j - i);
        for (double k = i; k <= j; k++)
        {
            var v = t.RawGet((int)Math.Clamp(k, int.MinValue, int.MaxValue));
            if (v != null && (v.Type == DataType.String)) n += v.String.Length;
            else if (v != null && v.Type == DataType.Number) n += 24;
        }
        return n;
    }

    static void Charge(Table t, string name, Guard guard, Func<CallbackArguments, double> size)
    {
        var orig = t.Get(name);
        if (orig.Type != DataType.ClrFunction) throw new InvalidOperationException($"MoonSharp has no C# '{name}' here (version change?)");
        var f = orig.Callback;
        t[name] = Fn(name, (c, a) => { guard.Charge(size(a), name); return f.ClrCallback(c, a); });
    }

    /// <summary>gsub: every replacement goes through a C# function that charges its length before gsub joins it (claim C5); the text
    /// gsub keeps is at most the subject's length. A string replacement is expanded here by Lua 5.2's rules (%0-%9, %%); the Euler pass
    /// checks this expansion against MoonSharp's own on random subjects, patterns and replacements.</summary>
    static void GuardGsub(Table str, Guard guard)
    {
        var orig = str.Get("gsub").Callback;
        str["gsub"] = Fn("gsub", (c, a) =>
        {
            if (a.Count < 3 || a[0].Type is not (DataType.String or DataType.Number) || a[1].Type is not (DataType.String or DataType.Number))
                return orig.ClrCallback(c, a);   // the original's own argument errors
            string subject = a[0].CastToString(), pattern = a[1].CastToString();
            var repl = a[2];
            guard.Charge(GsubSubjectCost * subject.Length, "gsub");   // MoonSharp's string library copies the subject into its buffers
            bool captures = PatternCaptures(pattern) > 0;
            DynValue fn;
            switch (repl.Type)
            {
                case DataType.String:
                case DataType.Number:
                {
                    string r = repl.CastToString();   // a bad '%' raises only when a match uses it, as MoonSharp's own does
                    fn = Repl(guard, (c2, a2) =>
                    {
                        // a2: the captures, or the whole match when the pattern has none
                        string whole = captures ? null! : a2.Count > 0 ? a2[0].CastToString() ?? "" : "";
                        var sb = new System.Text.StringBuilder();
                        for (int i = 0; i < r.Length; i++)
                        {
                            char ch = r[i];
                            if (ch != '%') { sb.Append(ch); continue; }
                            if (i + 1 >= r.Length) throw new ScriptRuntimeException("invalid use of '%' in replacement string");
                            char d = r[++i];
                            if (d == '%') { sb.Append('%'); continue; }
                            if (d < '0' || d > '9') throw new ScriptRuntimeException("invalid use of '%' in replacement string");
                            int k = d - '0';
                            string piece;
                            if (k == 0)
                            {
                                if (!captures) piece = whole;
                                else throw new GsubNeedsOriginal();
                            }
                            else if (!captures) piece = k == 1 ? whole : throw new ScriptRuntimeException($"invalid capture index %{k} in replacement string");
                            else piece = k <= a2.Count ? Plain(a2[k - 1]) : throw new ScriptRuntimeException($"invalid capture index %{k} in replacement string");
                            guard.Charge(2.0 * piece.Length, "gsub");
                            sb.Append(piece);
                        }
                        guard.Charge(2.0 * sb.Length, "gsub");
                        return DynValue.NewString(sb.ToString());
                    });
                    break;
                }
                case DataType.Table:
                {
                    var t = repl.Table;
                    fn = Repl(guard, (c2, a2) =>
                    {
                        var v = t.Get(a2.Count > 0 ? a2[0] : DynValue.Nil);
                        if (v.Type is DataType.String or DataType.Number) guard.Charge(2.0 * v.CastToString().Length, "gsub");
                        return v;
                    });
                    break;
                }
                case DataType.Function:
                case DataType.ClrFunction:
                {
                    fn = Repl(guard, (c2, a2) =>
                    {
                        var v = c2.Call(repl, a2.GetArray());
                        if (v.Type == DataType.Tuple) v = v.Tuple.Length > 0 ? v.Tuple[0] : DynValue.Nil;
                        if (v.Type is DataType.String or DataType.Number) guard.Charge(2.0 * v.CastToString().Length, "gsub");
                        return v;
                    });
                    break;
                }
                default:
                    return orig.ClrCallback(c, a);   // the original's own argument error
            }
            var args = a.GetArray();
            args[2] = fn;
            try { return orig.ClrCallback(c, new CallbackArguments(args.ToList(), a.IsMethodCall)); }
            catch (GsubNeedsOriginal)
            {   // %0 with captures: charged at the worst case (every match as long as the subject), then the original does it
                string r = repl.CastToString();
                double items = 0, literal = 0;
                for (int i = 0; i < r.Length; i++) { if (r[i] == '%' && i + 1 < r.Length && r[i + 1] != '%') { items++; i++; } else literal++; }
                double matches = subject.Length + 1;
                if (a.Count > 3 && a[3].Type == DataType.Number) matches = Math.Min(matches, Math.Max(0, Math.Floor(a[3].Number)));
                guard.Charge(2 * (subject.Length + matches * (literal + items * subject.Length)) + GsubMatchCost * matches, "gsub %0 with captures");
                return orig.ClrCallback(c, a);
            }
        });
    }

    sealed class GsubNeedsOriginal : Exception;

    /// <summary>Measured (tools/euler, C5.ratio): gsub allocates about 700 bytes per match and a few dozen per subject character.</summary>
    const double GsubMatchCost = 1000, GsubSubjectCost = 48;

    static DynValue Repl(Guard guard, Func<ScriptExecutionContext, CallbackArguments, DynValue> f) =>
        Fn("gsub.repl", (c, a) => guard.Clr(() => { guard.Charge(GsubMatchCost, "gsub match"); return f(c, a); }));

    /// <summary>Captures in a Lua pattern: '(' not escaped by '%' and not inside a [set].</summary>
    public static int PatternCaptures(string p)
    {
        int n = 0;
        for (int i = 0; i < p.Length; i++)
        {
            char ch = p[i];
            if (ch == '%') { i++; continue; }
            if (ch == '[')
            {
                i++;
                if (i < p.Length && p[i] == '^') i++;
                if (i < p.Length && p[i] == ']') i++;   // a ']' first in a set is a member
                while (i < p.Length && p[i] != ']') { if (p[i] == '%') i++; i++; }
                continue;
            }
            if (ch == '(') n++;
        }
        return n;
    }

    /// <summary>A C# function behind Guard.Clr. What it returns is guarded too: a C# function it hands back (coroutine.wrap's, the
    /// iterators of gmatch / ipairs / pairs) would otherwise be a way back into Lua that the depth count never sees (Euler attack
    /// coroutine.nested, 2026-11-22). One known difference: pairs(t) no longer returns the very same object as the global next.</summary>
    static DynValue Guarded(CallbackFunction f, Guard guard) =>
        DynValue.NewCallback(new CallbackFunction((c, a) => guard.Clr(() => GuardResult(f.ClrCallback(c, a), guard)), f.Name)
        {
            AdditionalData = f.AdditionalData,   // coroutine.wrap's function keeps its coroutine here (the context passes it on)
        });

    static DynValue GuardResult(DynValue r, Guard guard)
    {
        if (r == null) return r!;
        if (r.Type == DataType.ClrFunction) return Guarded(r.Callback, guard);
        if (r.Type == DataType.Tuple && r.Tuple.Any(x => x?.Type == DataType.ClrFunction))
            return DynValue.NewTuple(r.Tuple.Select(x => x?.Type == DataType.ClrFunction ? Guarded(x.Callback, guard) : x).ToArray());
        return r;
    }

    /// <summary>Replace every C# function in the library tables (and the string metatable's) by a guarded one, in place.</summary>
    static void WrapAll(Script s, Guard guard)
    {
        var seen = new HashSet<Table>(ReferenceEqualityComparer.Instance);
        void Walk(Table t)
        {
            if (!seen.Add(t)) return;
            foreach (var key in t.Keys.ToList())
            {
                var v = t.Get(key);
                if (v.Type == DataType.ClrFunction && v.Callback.Name is not "gsub.repl")
                {
                    var f = v.Callback;
                    t.Set(key, Guarded(f, guard));
                }
                else if (v.Type == DataType.Table) Walk(v.Table);
            }
            if (t.MetaTable != null) Walk(t.MetaTable);
        }
        Walk(s.Globals);
        var sm = s.GetTypeMetatable(DataType.String);
        if (sm != null) Walk(sm);
    }
}
