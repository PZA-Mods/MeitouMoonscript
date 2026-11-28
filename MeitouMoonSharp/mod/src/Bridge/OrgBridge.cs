// MeitouMoonscript: the org bridge's game side. Author: PZA. Licence: GPLv3 or later.
// One tick system polls the three org APIs in its serial step (SlowWorld), as their docs ask, and publishes an immutable snapshot; the
// pack capabilities dustlife.read, composer.read and twr.read read only that snapshot (claims O1-O5, tools/euler/CLAIMS.md). It changes
// nothing in the world and holds nothing the world needs: not hashed, not saved.
using Meitou.Modding;
using Meitou.Simulation;

namespace PZA.Moonscript.Bridge;

public sealed class OrgBridgeSystem : ITickSystem
{
    static Action<string>? s_log;
    static bool s_warned;
    public static readonly BridgePoller Poller = new(ApiReader.FindLoaded, s => s_log?.Invoke(s));

    public static void Attach(ModContext context, IPackExtensionHost host, Action<string> log)
    {
        s_log = log;
        context.AddSystem("pza.moonscript.bridge", _ => new OrgBridgeSystem(), SystemAnchor.End);
        host.RegisterPackCapability("pza.moonscript", BridgeFunctions.DustlifeCap, BridgeFunctions.Dustlife(() => Poller.Current));
        host.RegisterPackCapability("pza.moonscript", BridgeFunctions.ComposerCap, BridgeFunctions.Composer(() => Poller.Current));
        host.RegisterPackCapability("pza.moonscript", BridgeFunctions.TwrCap, BridgeFunctions.Twr(() => Poller.Current));
    }

    public void SlowWorld(World world)
    {
        try { Poller.Poll(world.GameHours); }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!s_warned) { s_warned = true; s_log?.Invoke($"bridge: a poll failed ({e.GetType().Name}); reads stay as they were"); } }
    }
}
