// MeitouMoonscript: plain values across the API (never MoonSharp's types). Author: PZA. Licence: GPLv3 or later.
//
// Exact rules (fuzz target value.convert): ToLua(FromLua(v)) keeps nil, booleans, numbers (to the bit), strings and tables of those in
// pairs() order; a function, coroutine or userdata becomes Nil; a table nested deeper than MaxDepth, or a cycle, becomes Nil there; at
// most MaxEntries table entries cross in one value (the rest are left out). Nothing here runs Lua or a metamethod.
using MoonSharp.Interpreter;

namespace PZA.Moonscript.Core;

public enum ScriptKind { Nil, Boolean, Number, String, Table }

public sealed class ScriptValue
{
    public const int MaxDepth = 32, MaxEntries = 100_000;

    public static readonly ScriptValue Nil = new(ScriptKind.Nil, false, 0, null, null);
    public static readonly ScriptValue True = new(ScriptKind.Boolean, true, 0, null, null);
    public static readonly ScriptValue False = new(ScriptKind.Boolean, false, 0, null, null);

    public ScriptKind Kind { get; }
    public bool Boolean { get; }
    public double Number { get; }
    public string? String { get; }
    public IReadOnlyList<KeyValuePair<ScriptValue, ScriptValue>>? Table { get; }

    ScriptValue(ScriptKind kind, bool b, double n, string? s, IReadOnlyList<KeyValuePair<ScriptValue, ScriptValue>>? t)
    { Kind = kind; Boolean = b; Number = n; String = s; Table = t; }

    public static ScriptValue Of(bool b) => b ? True : False;
    public static ScriptValue Of(double n) => new(ScriptKind.Number, false, n, null, null);
    public static ScriptValue Of(string? s) => s is null ? Nil : new(ScriptKind.String, false, 0, s, null);
    public static ScriptValue Of(IEnumerable<KeyValuePair<ScriptValue, ScriptValue>> pairs) => new(ScriptKind.Table, false, 0, null, pairs.ToList());
    public static ScriptValue List(params ScriptValue[] items) => Of(items.Select((v, i) => new KeyValuePair<ScriptValue, ScriptValue>(Of(i + 1), v)));

    public bool IsNil => Kind == ScriptKind.Nil;

    /// <summary>table[key] for a string or number key (first match), Nil when absent.</summary>
    public ScriptValue this[string key] => Table?.FirstOrDefault(p => p.Key.Kind == ScriptKind.String && p.Key.String == key).Value ?? Nil;
    public ScriptValue this[double key] => Table?.FirstOrDefault(p => p.Key.Kind == ScriptKind.Number && p.Key.Number == key).Value ?? Nil;

    public override string ToString() => Kind switch
    {
        ScriptKind.Nil => "nil",
        ScriptKind.Boolean => Boolean ? "true" : "false",
        ScriptKind.Number => SafeLib.NumberText(Number),
        ScriptKind.String => String!,
        _ => "table",
    };

    // ---- conversion (only on the Lua thread) ----

    public static ScriptValue FromLua(DynValue? v)
    {
        int budget = MaxEntries;
        return From(v, 0, new HashSet<Table>(ReferenceEqualityComparer.Instance), ref budget);
    }

    static ScriptValue From(DynValue? v, int depth, HashSet<Table> path, ref int budget)
    {
        if (v is null) return Nil;
        if (v.Type == DataType.Tuple) return v.Tuple.Length > 0 ? From(v.Tuple[0], depth, path, ref budget) : Nil;
        switch (v.Type)
        {
            case DataType.Boolean: return Of(v.Boolean);
            case DataType.Number: return Of(v.Number);
            case DataType.String: return Of(v.String);
            case DataType.Table:
            {
                if (depth >= MaxDepth || !path.Add(v.Table)) return Nil;
                var list = new List<KeyValuePair<ScriptValue, ScriptValue>>();
                foreach (var p in v.Table.Pairs)
                {
                    if (p.Value.IsNil()) continue;
                    if (--budget < 0) break;
                    var k = From(p.Key, depth + 1, path, ref budget);
                    var x = From(p.Value, depth + 1, path, ref budget);
                    if (!k.IsNil && !x.IsNil) list.Add(new(k, x));
                }
                path.Remove(v.Table);
                return new ScriptValue(ScriptKind.Table, false, 0, null, list);
            }
            default: return Nil;
        }
    }

    public DynValue ToLua(Script s, int depth = 0) => Kind switch
    {
        ScriptKind.Boolean => DynValue.NewBoolean(Boolean),
        ScriptKind.Number => DynValue.NewNumber(Number),
        ScriptKind.String => DynValue.NewString(String),
        ScriptKind.Table when depth < MaxDepth => DynValue.NewTable(TableToLua(s, depth)),
        _ => DynValue.Nil,
    };

    Table TableToLua(Script s, int depth)
    {
        var t = new Table(s);
        foreach (var p in Table!)
        {
            if (p.Key.IsNil || p.Value.IsNil || (p.Key.Kind == ScriptKind.Number && double.IsNaN(p.Key.Number))) continue;
            t.Set(p.Key.ToLua(s, depth + 1), p.Value.ToLua(s, depth + 1));
        }
        return t;
    }
}
