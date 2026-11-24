// MeitouMoonscript: the ScriptHost API, version 1. Author: PZA. Licence: GPLv3 or later.
//
// For other mods: declare the dependency in meitou.json ("dependencies": [{ "id": "pza.moonscript", "version": ">=0.1.0 <0.2.0" }]),
// reference MeitouMoonscript.dll with Private="false", then in Initialize:
//     var host = context.Dependency<IScriptHost>("pza.moonscript")!;   // host.ApiVersion == 1
// and, per world (in your system's factory), open your scripts:
//     var s = host.Open(new ScriptSpec { Owner = "my.mod", Name = "ladder", Folder = context.Folder, File = "scripts/ladder.lua", Seed = seed });
// Call s.BeginTick(world.Tick) before a tick's calls; queue world changes as your own commands from what the calls return; write s.Save()
// in your IModSaveData and s.Restore(bytes) after a load; add s.StateHash() to your IStateHashed. Only plain values cross (ScriptValue),
// never MoonSharp's types: MoonSharp stays private to this mod. Contract and limits: README "ScriptHost API" and tools/euler/CLAIMS.md.
// Compatibility: v1 members are never removed or changed; a later version adds members and raises ApiVersion.
using PZA.Moonscript.Core;

namespace PZA.Moonscript;

public interface IScriptHost
{
    /// <summary>1 for this contract.</summary>
    int ApiVersion { get; }

    /// <summary>The host mod's version (meitou.json).</summary>
    string HostVersion { get; }

    /// <summary>Compile and run a script's text (its top level is one budgeted call). Never throws for a bad script: the result is faulted.</summary>
    IScript Open(ScriptSpec spec);
}

public sealed class ScriptSpec
{
    /// <summary>The calling mod's id: in every log line about the script.</summary>
    public required string Owner { get; init; }

    /// <summary>The script's name in log lines.</summary>
    public required string Name { get; init; }

    /// <summary>The mod folder File is read from (a file outside it is refused).</summary>
    public string? Folder { get; init; }

    /// <summary>A .lua file inside Folder, relative ("scripts/ladder.lua"). Or give Text.</summary>
    public string? File { get; init; }

    /// <summary>The script's text, when there is no file.</summary>
    public string? Text { get; init; }

    /// <summary>The seed of math.random: take it from the world (your mod's RngPurpose), never from the clock.</summary>
    public ulong Seed { get; init; }

    /// <summary>Budgets and caps (null: the defaults, ScriptLimits).</summary>
    public ScriptLimits? Limits { get; init; }

    /// <summary>Where print() goes (null: the host's log).</summary>
    public Action<string>? Print { get; init; }

    /// <summary>Development only: re-read File when it changes, applied at the next BeginTick (before that tick's first call). Reading the
    /// file's time is not deterministic: leave it off for a released game's simulation scripts.</summary>
    public bool HotReload { get; init; }

    /// <summary>The narrow C# surface the script may call, as table.name (table "" = a global).</summary>
    public IReadOnlyList<HostFunction> Functions { get; init; } = [];
}

/// <summary>A function a script may call. It runs on the host's Lua thread inside the script's call: read only values you were given
/// (a snapshot), never the World.</summary>
public sealed record HostFunction(string Table, string Name, Func<IReadOnlyList<ScriptValue>, ScriptValue> Call);

public interface IScript
{
    string Name { get; }

    /// <summary>Null, or why the script was switched off for this world: budget, tick-budget, memory, memory-hard, stack, error, syntax,
    /// save, clr. A faulted script never runs again; every call returns Nil.</summary>
    string? Fault { get; }

    string? FaultDetail { get; }

    long FaultTick { get; }

    /// <summary>Instructions the last call ran.</summary>
    long LastCallInstructions { get; }

    /// <summary>Call before a tick's calls (the tick budget restarts; a pending hot reload is applied here).</summary>
    void BeginTick(long tick);

    /// <summary>Whether the script defines a global function of that name.</summary>
    bool Has(string function);

    /// <summary>Call a global function with plain values; its first result as a plain value (Nil when faulted or absent).</summary>
    ScriptValue Call(string function, params ScriptValue[] args);

    /// <summary>Run a piece of text as one budgeted call (a terminal's line).</summary>
    ScriptValue Eval(string code);

    /// <summary>The script's `state` table and its random state, for your save. Runs no Lua.</summary>
    byte[] Save();

    /// <summary>Put a save back (then the script's on_load() runs). False, with the script faulted "save", for a malformed one.</summary>
    bool Restore(byte[] saved);

    /// <summary>For your IStateHashed: the saved state, the fault and its tick, the instructions run.</summary>
    ulong StateHash();

    /// <summary>Re-read File now (host or UI side, between ticks). The new text must load; the state carries over through on_load().
    /// False (the old script keeps running) when it does not; ReloadNote says why.</summary>
    bool Reload();

    /// <summary>The last reload's result, for a log or a terminal.</summary>
    string? ReloadNote { get; }
}
