// MeitouMoonscript: the code mod's entry (meitou.json "entry") and the ScriptHost API v1 other mods depend on. Author: PZA. Licence: GPLv3 or later.
//
// One host for everyone (MeitouClient docs/modding-dependencies.md): MoonSharp loads once, in this mod's own load context, and stays private:
// only MeitouMoonscript.dll is in meitou.json's "assemblies", so a dependent sees our API (Api.cs) and never MoonSharp's types.
// Anything missing in the game -> that part stays off with a line saying why; the game goes on.
using System.Reflection;
using Meitou.Modding;
using Meitou.Simulation;
using PZA.MeitouUi;
using PZA.Moonscript.Core;
using PZA.Moonscript.KenshiLua;
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public sealed class HostMod : IMod, IScriptHost, IPackExtensionHost, IModDependents
{
    public static readonly HostSettings Settings = new();
    static ModSettingsGroup? s_group;
    static Action<string>? s_log;
    static PackHost? s_packs;
    static KenshiLuaHost? s_kl;
    static readonly PackStore s_store = new();
    static readonly object s_worldLock = new();
    static (int World, Dictionary<string, byte[]> Entries, bool FromSave)? s_pendingWorld;
    static volatile int s_appliedWorld;
    static int s_lastBagHour = int.MinValue;
    static readonly List<(string Owner, string Cap, IReadOnlyList<HostFunction> Fns)> s_pendingExtensions = [];
    static readonly Dictionary<string, int> s_opened = new(StringComparer.Ordinal);

    public int ApiVersion => 1;
    public string HostVersion { get; } = Version();

    public static void Line(string s) => s_log?.Invoke(s);

    static string Version()
    {
        string v = typeof(HostMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        int plus = v.IndexOf('+');
        return plus >= 0 ? v[..plus] : v;
    }

    public void Initialize(ModContext context)
    {
        s_log = context.Log;
        s_group = context.AddSettings(Settings);
        var kept = PzaSettings.Keep(s_group, context.Folder);
        Line($"{HostVersion} by PZA: a Lua host for Meitou mods (ScriptHost API {ApiVersion}, MoonSharp {MoonSharp.Interpreter.Script.VERSION}); Shift+Y: the terminal");
        foreach (var l in kept) Line(l);
        if (!LiveSize.CanSeeCoroutines) Line("warning: this MoonSharp build's coroutine internals moved: data in suspended coroutines is not counted (tools/euler C5.live)");

        context.AddSystem("pza.moonscript.snapshot", b => new WorldSnapshot(b.Find<Meitou.Simulation.Aggro.AggroSystem>()?.Relations), SystemAnchor.End);
        context.AddSystem("pza.moonscript.packs", _ => new PackSaveSystem(s_store, Line), SystemAnchor.End);
        if (Directory.Exists(Path.Combine(context.Folder, "pack_states")))
            Line("the pack_states folder is from before 0.12.0 (one per install) and is no longer read: each save keeps its own pack state now; the folder is left as it was");
        Bridge.OrgBridgeSystem.Attach(context, this, Line);   // read-only views of Dustlife, the Composer and TWR for packs (claims O1-O5)
        StartRepl(context);
        context.AddDebugCommand("Moonscript terminal: open / close (Shift+Y)", _ => { ReplWindow.Toggle(); return "terminal"; });
        context.AddDebugCommand("Moonscript terminal: play the demo", _ => { ReplWindow.Demo(); return "terminal demo"; });
        context.AddDebugCommand("Moonscript battle: 5 Hungry Bandits", _ => { BattleWindow.Start("hungry"); return "battle"; });
        context.AddDebugCommand("Moonscript crawlers: open Cat Crawlers table", _ => { CrawlersWindow.Open(); return "crawlers"; });
    }

    void StartRepl(ModContext context)
    {
        string scripts = Path.Combine(context.Folder, "scripts");
        ReplWindow.Session = new ReplSession(scripts, () => WorldSnapshot.Hours, () => WorldSnapshot.SelectedName, ConfigEntries,
            () => new ScriptLimits { InstructionsPerCall = (long)(Settings.ReplBudgetK * 1000), InstructionsPerTick = long.MaxValue / 4 },
            Line, Settings.Banner, () => WorldSnapshot.Squad, () => WorldSnapshot.Cats);
        ReplWindow.Setup(Settings, Set, context.Folder);
        BattleWindow.Session = new BattleSession(Path.Combine(context.Folder, "scripts", "fight.lua"), () => WorldSnapshot.Squad, Line);
        BattleWindow.SquadIds = () => WorldSnapshot.SquadIds;
        BattleWindow.Setup(context.Folder, Settings, Set);
        ReplWindow.Session!.Core.OpenBattle = BattleWindow.Start;
        CrawlersWindow.Session = new CrawlersSession(Path.Combine(context.Folder, "scripts", "crawlers.lua"), () => WorldSnapshot.SelectedName, () => WorldSnapshot.Cats, Line);
        CrawlersWindow.Setup(context.Folder);
        ReplWindow.Session!.Core.OpenCrawlers = CrawlersWindow.Open;
        HudWindow.Setup();
        string? modsRoot = Directory.GetParent(context.Folder)?.FullName;
        string selfFolder = Path.GetFileName(context.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var packs = new PackHost(Path.Combine(context.Folder, "packs"), () => WorldSnapshot.Hours, () => WorldSnapshot.SelectedName,
            () => WorldSnapshot.Squad, () => WorldSnapshot.Cats,
            () => new ScriptLimits { InstructionsPerCall = (long)(Settings.ReplBudgetK * 1000), InstructionsPerTick = long.MaxValue / 4 },
            ReplWindow.Session.Core.Out, Line, modsRoot, selfFolder, Path.Combine(context.Folder, "packs.grants.json"),
            () => WorldSnapshot.Scan, WorldSnapshot.EnqueueAction, s_store,
            () => WorldSnapshot.Camera, HudWindow.State);
        var klHost = new KenshiLuaHost(modsRoot, selfFolder, Path.Combine(context.Folder, "kenshilua.grants.json"),
            () => WorldSnapshot.Hours, () => WorldSnapshot.Cats, () => WorldSnapshot.SelectedName,
            () => WorldSnapshot.Scan, () => WorldSnapshot.Camera, WorldSnapshot.EnqueueAction, HudWindow.State,
            () => new ScriptLimits { InstructionsPerCall = (long)(Settings.ReplBudgetK * 1000), InstructionsPerTick = long.MaxValue / 4 },
            ReplWindow.Session.Core.Out, Line, s_store,
            viewSize: () => (HudWindow.ViewWidth, HudWindow.ViewHeight));
        s_packs = packs;
        s_kl = klHost;
        HudWindow.OnButtonClick = (owner, btnId) =>
        {
            packs.DispatchHudClick(owner, btnId);
            klHost.DispatchHudClick(owner, btnId);
        };
        lock (s_pendingExtensions)
        {
            foreach (var (owner, cap, fns) in s_pendingExtensions) packs.RegisterExtension(owner, cap, fns);
            s_pendingExtensions.Clear();
        }
        ReplWindow.Session.Core.Packs = packs;
        ReplWindow.Session.Core.KenshiLua = klHost;
        ReplWindow.Session.Core.KenshiLuaRetired = BuiltInLua;
        foreach (var (m, _, _, _) in packs.List())
            if (!m.ThirdParty || Settings.AutoStartThirdPartyPacks) packs.Start(m.Id);
        if (BuiltInLua)
        {
            Line("KenshiLua layer retired here: Meitou 0.24.0+ runs KenshiLua mods itself (Meitou.Lua); the console, packs and minigames stay on");
        }
        else if (Settings.AutoStartKenshiLua)
        {
            var klStarted = klHost.StartEnabledFromModsCfg();
            if (klStarted.Count > 0)
                Line($"KenshiLua compat auto-started {klStarted.Count} mod(s) from mods.cfg: {string.Join(", ", klStarted)}");
        }
        PzaUi.Log = Line;
        PzaUi.EachFrame = () =>
        {
            WorldSnapshot.SetSelection(PzaUi.SelectedCharacters());
            ReplWindow.EachFrame(Settings);
            BattleWindow.EachFrame();
            HudWindow.EachFrame();
            ApplyWorld();
            if (WorldSnapshot.Hours is double gh && (int)Math.Floor(gh) != s_lastBagHour && s_appliedWorld != 0)
            {
                s_lastBagHour = (int)Math.Floor(gh);
                klHost.SerialiseRunning();
            }
            var evs = WorldSnapshot.DrainEvents();
            packs.Tick(WorldSnapshot.Hours, evs);
            klHost.Tick(evs);
        };
        PzaUi.Install(context);
        foreach (var l in ReplKeys.Install(context.Harmony)) Line(l);
        ReplKeys.Pressed = ReplWindow.Toggle;
        ReplKeys.OnKeyDown = klHost.NotifyKeyDown;
    }

    /// <summary>Meitou 0.24.0+ ships its own KenshiLua runtime (VlcekM/MeitouClient#1494, Meitou.Lua): Moonscript's layer is retired there.</summary>
    public static readonly bool BuiltInLua = HasBuiltInMeitouLua();

    static bool HasBuiltInMeitouLua() =>
        typeof(ModContext).Assembly.GetType("Meitou.Modding.LuaMods", throwOnError: false) is not null
        || Type.GetType("Meitou.Lua.KenshiLuaHost, Meitou.Lua", throwOnError: false) is not null;

    /// <summary>World side (PackSaveSystem's first tick): a new world runs, with these saved entries (none for a new game).</summary>
    internal static void QueueWorld(int world, Dictionary<string, byte[]> entries, bool fromSave)
    {
        lock (s_worldLock) s_pendingWorld = (world, entries, fromSave);
    }

    /// <summary>The world whose entries the store holds now; a save of any other world writes what it loaded with.</summary>
    internal static int AppliedWorld => s_appliedWorld;

    /// <summary>Interface side, each frame: swap the packs over to a new world's state (C7b).</summary>
    static void ApplyWorld()
    {
        (int World, Dictionary<string, byte[]> Entries, bool FromSave) w;
        lock (s_worldLock)
        {
            if (s_pendingWorld is not { } p) return;
            w = p; s_pendingWorld = null;
        }
        var packs = s_packs?.StopRunning() ?? [];
        var mods = s_kl?.StopRunning() ?? [];
        s_store.Replace(w.Entries);
        s_appliedWorld = w.World;
        s_lastBagHour = int.MinValue;
        int a = s_packs?.StartAgain(packs).Count ?? 0, b = s_kl?.StartAgain(mods).Count ?? 0;
        Line(w.FromSave
            ? $"packs: this save's state loaded ({w.Entries.Count} entr{(w.Entries.Count == 1 ? "y" : "ies")}); {a} pack(s) and {b} KenshiLua mod(s) restarted"
            : $"packs: a new world, so packs start clean; {a} pack(s) and {b} KenshiLua mod(s) restarted");
    }

    static void Set(string key, object value) => PzaSettings.Set(s_group, Settings, key, value);

    static IReadOnlyList<ConfigEntry> ConfigEntries() =>
    [
        ReplSession.Switch("typing", "type answers out a few letters at a time", () => Settings.Typing, v => Set(nameof(HostSettings.Typing), v)),
        ReplSession.Number("typing.speed", "letters a frame while typing (1-40)", () => Settings.TypingSpeed, v => Set(nameof(HostSettings.TypingSpeed), Math.Round(v)), 1, 40),
        ReplSession.Switch("banner", "the title banner when the terminal starts", () => Settings.Banner, v => Set(nameof(HostSettings.Banner), v)),
        ReplSession.Number("budget.call", "thousands of Lua instructions per terminal line (1-2000)", () => Settings.ReplBudgetK, v => Set(nameof(HostSettings.ReplBudgetK), Math.Round(v)), 1, 2000),
        ReplSession.Switch("packs.autostart", "auto-start third-party Lua packs from other mods' moonscript/ folders", () => Settings.AutoStartThirdPartyPacks, v => Set(nameof(HostSettings.AutoStartThirdPartyPacks), v)),
        ReplSession.Switch("kenshilua.autostart", "auto-start KenshiLua mods enabled in data/mods.cfg", () => Settings.AutoStartKenshiLua, v => Set(nameof(HostSettings.AutoStartKenshiLua), v)),
    ];

    // ---------------------------------------------------------------- ScriptHost API v1

    public IScript Open(ScriptSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        lock (s_opened) s_opened[spec.Owner] = s_opened.GetValueOrDefault(spec.Owner) + 1;
        var s = new HostedScript(spec, Line);
        if (s.Fault != null) Line($"{spec.Owner}/{spec.Name}: did not load: {s.Fault}: {s.FaultDetail}");
        return s;
    }

    /// <summary>A dependent's Initialize threw (docs/modding-dependencies.md "Failed initialization"): we keep nothing of theirs.</summary>
    public void DependentFailed(string modId) => Line($"dependent {modId} failed to initialize; its scripts are its own and stop with it");

    public bool RegisterPackCapability(string owner, string capability, IReadOnlyList<HostFunction> functions)
    {
        if (s_packs is { } p) return p.RegisterExtension(owner, capability, functions);
        lock (s_pendingExtensions) s_pendingExtensions.Add((owner, capability, functions));
        return true;
    }
}
