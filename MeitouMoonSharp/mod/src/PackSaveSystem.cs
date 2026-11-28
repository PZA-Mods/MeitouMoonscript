// MeitouMoonscript: puts the Lua packs' saved state into each save (claim C7b, tools/euler/CLAIMS.md). Author: PZA. Licence: GPLv3 or later.
//
// One of these is made for every world (ModContext.AddSystem). Meitou writes it into the save's sidecar for pza.moonscript and reads it back
// into the next world's system on a load (IModSaveData; a new game gets no Read). At its first tick it tells the host which entries the new
// world has; the host swaps the packs over on the interface side (HostMod.ApplyWorld). Until then a save writes the entries this world
// loaded with, so a save made straight after a load keeps what it loaded.
using Meitou.Modding;
using Meitou.Simulation;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public sealed class PackSaveSystem : ITickSystem, IModSaveData
{
    static int s_lastWorld;
    readonly int _world = Interlocked.Increment(ref s_lastWorld);
    readonly PackStore _store;
    readonly Action<string> _log;
    Dictionary<string, byte[]> _loaded = new(StringComparer.Ordinal);
    bool _fromSave, _told;

    public PackSaveSystem(PackStore store, Action<string> log) { _store = store; _log = log; }

    public void Read(Stream stream)
    {
        try { _loaded = PackStore.Read(stream); _fromSave = true; }
        catch (FormatException e) { _loaded = new(StringComparer.Ordinal); _fromSave = true; _log($"this save's pack state was not read ({e.Message}): packs start clean"); }
    }

    public void SlowWorld(World world)
    {
        if (_told) return;
        _told = true;
        HostMod.QueueWorld(_world, _loaded, _fromSave);
    }

    public void Write(Stream stream) => PackStore.Write(stream, HostMod.AppliedWorld == _world ? _store.Snapshot() : _loaded);
}
