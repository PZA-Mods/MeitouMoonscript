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
using PZA.Moonscript.Repl;

namespace PZA.Moonscript;

public sealed class HostMod : IMod, IScriptHost, IModDependents
{
    public static readonly HostSettings Settings = new();
    static ModSettingsGroup? s_group;
    static Action<string>? s_log;
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

        context.AddSystem("pza.moonscript.snapshot", _ => new WorldSnapshot(), SystemAnchor.End);
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
        string? modsRoot = Directory.GetParent(context.Folder)?.FullName;
        string selfFolder = Path.GetFileName(context.Folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var packs = new PackHost(Path.Combine(context.Folder, "packs"), () => WorldSnapshot.Hours, () => WorldSnapshot.SelectedName,
            () => WorldSnapshot.Squad, () => WorldSnapshot.Cats,
            () => new ScriptLimits { InstructionsPerCall = (long)(Settings.ReplBudgetK * 1000), InstructionsPerTick = long.MaxValue / 4 },
            ReplWindow.Session.Core.Out, Line, modsRoot, selfFolder, Path.Combine(context.Folder, "packs.grants.json"));
        ReplWindow.Session.Core.Packs = packs;
        foreach (var (m, _, _, _) in packs.List())
            if (!m.ThirdParty || Settings.AutoStartThirdPartyPacks) packs.Start(m.Id);
        PzaUi.Log = Line;
        PzaUi.EachFrame = () =>
        {
            WorldSnapshot.SetSelection(PzaUi.SelectedCharacters());
            ReplWindow.EachFrame(Settings);
            BattleWindow.EachFrame();
            packs.Tick(WorldSnapshot.Hours, WorldSnapshot.DrainEvents());
        };
        PzaUi.Install(context);
        foreach (var l in ReplKeys.Install(context.Harmony)) Line(l);
        ReplKeys.Pressed = ReplWindow.Toggle;
    }

    static void Set(string key, object value) => PzaSettings.Set(s_group, Settings, key, value);

    static IReadOnlyList<ConfigEntry> ConfigEntries() =>
    [
        ReplSession.Switch("typing", "type answers out a few letters at a time", () => Settings.Typing, v => Set(nameof(HostSettings.Typing), v)),
        ReplSession.Number("typing.speed", "letters a frame while typing (1-40)", () => Settings.TypingSpeed, v => Set(nameof(HostSettings.TypingSpeed), Math.Round(v)), 1, 40),
        ReplSession.Switch("banner", "the title banner when the terminal starts", () => Settings.Banner, v => Set(nameof(HostSettings.Banner), v)),
        ReplSession.Number("budget.call", "thousands of Lua instructions per terminal line (1-2000)", () => Settings.ReplBudgetK, v => Set(nameof(HostSettings.ReplBudgetK), Math.Round(v)), 1, 2000),
        ReplSession.Switch("packs.autostart", "auto-start third-party Lua packs from other mods' moonscript/ folders", () => Settings.AutoStartThirdPartyPacks, v => Set(nameof(HostSettings.AutoStartThirdPartyPacks), v)),
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
}
