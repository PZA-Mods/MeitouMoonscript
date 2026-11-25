// PZA's Meitou UI: the shared window library for PZA's Meitou mods. Author: PZA. Licence: GPLv3 or later.
// A SOURCE library: each mod compiles these files into its own assembly (<Compile Include="..\..\PZAMeitouUi\src\*.cs" />), so each mod has its
// own copy, its own state and its own Harmony patches; nothing is shared between mods at run time and two mods can never clash.
// In-game proof: PZA's UI Probe (MyGuiScreenHost, confirmed 2026-11-16).
//
// PzaUi is the hub:
//   Install(context)   once in IMod.Initialize: resolves the game's members by name (a miss = no windows, one log line, the mod goes on),
//                      patches PlayerInterface.Steer (prefix: once a frame on the host thread), ScreenRouter.BindFaithfulScreen (postfix: our
//                      windows' bindings), GameHost.GatherPortraitKeys (prefix: character faces, PzaPortrait; optional) and, when tiny buttons
//                      are used, the four pointer calls; registers the HUD extension.
//   Add(window) / Add(tinyButton)   any time before or after Install.
// Every frame (Steer prefix): the player's MyGuiScreenHost is found, our windows registered on it, their queued open / close applied, their
// places watched and saved. Everything runs on the host thread; nothing here touches the World.
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Ui;
using Meitou.Modding;

namespace PZA.MeitouUi;

public static class PzaUi
{
    /// <summary>The library's version (written in the log at Install).</summary>
    public const string Version = "0.3.3";

    /// <summary>Where the library's lines go (the mod sets it, usually to its own log with its prefix).</summary>
    public static Action<string> Log = _ => { };

    static readonly List<PzaWindow> s_windows = [];
    static readonly List<PzaTinyButton> s_tiny = [];
    static bool s_installed, s_pointerPatched, s_hostLogged;
    static ModContext? s_context;

    /// <summary>The player's interface seen last (internal Meitou.Game.PlayerInterface), and its window host; null before the first frame.</summary>
    public static object? Player { get; private set; }
    public static MyGuiScreenHost? Host { get; private set; }
    /// <summary>The game window's size in pixels (PlayerInterface.Width / Height), last frame.</summary>
    public static int Width { get; private set; } = 1920;
    public static int Height { get; private set; } = 1080;

    /// <summary>A text box (ours or the game's) has the keyboard: hotkeys must stand down (PZA UI rules 4).</summary>
    public static bool Typing => (Host?.HasKeyboardFocus ?? false) || (Player is not null && Access.UiEditing is not null && (bool)Access.UiEditing.GetValue(Player)!);

    /// <summary>A game is loaded and played: not the title screen (Meitou's main menu, PlayerInterface.TitleOpen) and the world has a player
    /// (World.Player.Exists). Until then (and on the title screen of a later session) PZA windows and tiny buttons are not shown: a window
    /// opened or left open there comes back once a game is running. True when the game lacks a member (the old behaviour) and before the first
    /// frame. <see cref="InGameOverride"/> replaces it (the offline check).</summary>
    public static bool InGame
    {
        get
        {
            if (InGameOverride is { } o) return o();
            if (Player is not { } p) return true;
            if (Access.TitleOpen?.GetValue(p) is true) return false;
            return Access.Session is null || Access.Session(p) is not { } s || s.World.Player.Exists;
        }
    }
    /// <summary>For the offline check: replaces <see cref="InGame"/>.</summary>
    public static Func<bool>? InGameOverride { get; set; }

    /// <summary>Called once a frame on the host thread after the windows (a mod's own per-frame work, e.g. its counter line).</summary>
    public static Action? EachFrame { get; set; }

    public static IReadOnlyList<PzaWindow> Windows => s_windows;

    /// <summary>The player's selected characters (PlayerInterface.SelectedIds), empty before the first frame or when the game lacks it.</summary>
    public static IReadOnlyList<Meitou.Simulation.CharacterId> SelectedCharacters() =>
        Player is not null && Access.SelectedIds?.Invoke(Player, null) is IReadOnlyList<Meitou.Simulation.CharacterId> ids ? ids : [];
    public static IReadOnlyList<PzaTinyButton> TinyButtons => s_tiny;

    public static void Add(PzaWindow w)
    {
        if (s_windows.Any(x => x.Id == w.Id)) throw new ArgumentException("a window with id " + w.Id + " is already added");
        s_windows.Add(w);
        if (w.Minimizable && s_installed && s_harmony is not null) PatchPointer(s_harmony);
    }

    public static void Add(PzaTinyButton t)
    {
        s_tiny.Add(t);
        if (s_installed && s_harmony is not null) PatchPointer(s_harmony);
    }

    /// <summary>Resolves, patches and registers. Returns false (with a line) when the game lacks something: the mod then has no windows.</summary>
    public static bool Install(ModContext context) => Install(context.Harmony, context);

    /// <summary>The same with a bare Harmony (the offline check; no HUD extension without a context).</summary>
    public static bool Install(Harmony harmony, ModContext? context = null)
    {
        if (s_installed) return true;
        // 0.3.3: the library patches under its OWN Harmony id (the host's id + ".pzaui"), so a failed install unpatches only the
        // library's patches; 0.3.2 called UnpatchAll on the host mod's id and took the host's own patches with it (review, 2026-11-22).
        harmony = s_harmony ??= new Harmony(harmony.Id + ".pzaui");
        var missing = Access.ResolveAll();
        if (missing.Count > 0)
        {
            Log("PZA's Meitou UI " + Version + ": no windows, the game lacks " + string.Join("; ", missing) + " (built for Meitou 0.20.1)");
            return false;
        }
        try
        {
            harmony.Patch(Access.Steer, prefix: Hook(nameof(SteerPrefix)));
            harmony.Patch(Access.BindFaithfulScreen, postfix: Hook(nameof(BindPostfix)));
            s_context = context;
            s_installed = true;
            if (s_tiny.Count > 0 || s_windows.Any(w => w.Minimizable)) PatchPointer(harmony);
            bool faces = PzaPortrait.Install(harmony);
            context?.AddUi(new PzaHud(context.Id));
            Log($"PZA's Meitou UI {Version}: patched PlayerInterface.Steer, ScreenRouter.BindFaithfulScreen{(s_pointerPatched ? ", the four pointer calls" : "")}{(faces ? ", GameHost.GatherPortraitKeys (faces)" : "")}; {s_windows.Count} window(s), {s_tiny.Count} tiny button(s)");
            return true;
        }
        catch (Exception e)
        {
            harmony.UnpatchAll(harmony.Id);
            Log("PZA's Meitou UI " + Version + ": no windows, patching failed (" + e.GetType().Name + ": " + e.Message + ")");
            return false;
        }
    }

    /// <summary>The library's own Harmony (the host's id + ".pzaui"), made on the first Install.</summary>
    static Harmony? s_harmony;

    /// <summary>The library's Harmony id once installed (for the offline check: it must not be the host's).</summary>
    public static string? HarmonyId => s_harmony?.Id;

    static void PatchPointer(Harmony harmony)
    {
        if (s_pointerPatched) return;
        harmony.Patch(Access.PointerDown, prefix: Hook(nameof(DownPrefix)));
        harmony.Patch(Access.PointerMove, prefix: Hook(nameof(MovePrefix)));
        harmony.Patch(Access.PointerUp, prefix: Hook(nameof(UpPrefix)));
        harmony.Patch(Access.UiScroll, prefix: Hook(nameof(ScrollPrefix)));
        s_pointerPatched = true;
    }

    static HarmonyMethod Hook(string name) => new(AccessTools.Method(typeof(PzaUi), name));

    /// <summary>One frame for a host (the Steer prefix calls it with the player's; the offline check with its own).</summary>
    public static void Frame(MyGuiScreenHost host, int width, int height)
    {
        Host = host;
        if (width > 0) Width = width;
        if (height > 0) Height = height;
        foreach (var w in s_windows)
        {
            try { w.Frame(host, Width, Height); }
            catch (Exception e) { Guard.Fault(w.Id, "frame", e); }
        }
        if (EachFrame is { } each) Guard.Run("mod", "each frame", each);
    }

    static void SteerPrefix(object __instance)
    {
        try
        {
            Player = __instance;
            var host = Access.FaithfulScreens(__instance);
            if (host is null) return;
            if (!s_hostLogged)
            {
                s_hostLogged = true;
                Log($"PZA's Meitou UI: the player's window host is ready ({host.Registered.Count} game screens)");
            }
            Frame(host, (int)Access.Width.GetValue(__instance)!, (int)Access.Height.GetValue(__instance)!);
        }
        catch (Exception e) { Guard.Fault("library", "frame", e); }
    }

    static void BindPostfix(string id, UiBindings __result)
    {
        foreach (var w in s_windows)
            if (w.Id == id)
            {
                try { w.ApplyBind(__result); }
                catch (Exception e) { Guard.Fault(w.Id, "bind", e); }
                return;
            }
    }

    // ---- the pointer over the interface (GameLoop hands it to these PlayerInterface calls and keeps it out of the control tick's input) ----
    static void DownPrefix(MouseButton button, Vector2 p) => PointerDown(button, p);

    /// <summary>A press over the interface (the MouseDown prefix; public for offline checks): the tiny buttons and the minimizable windows' title.</summary>
    public static void PointerDown(MouseButton button, Vector2 p)
    {
        foreach (var t in s_tiny) Guard.Run(t.Id, "press", () => t.PointerDown(button, p));
        foreach (var w in s_windows) if (w.Minimizable) Guard.Run(w.Id, "title press", () => w.PointerDown(button, p));
    }
    static void MovePrefix(Vector2 p) { foreach (var t in s_tiny) Guard.Run(t.Id, "move", () => t.PointerMove(p)); }
    static void UpPrefix(MouseButton button, Vector2 p) { foreach (var t in s_tiny) Guard.Run(t.Id, "release", () => t.PointerUp(button, p)); }

    /// <summary>false (and __result true: the game's scroll and the camera's zoom skip that notch) only for a wheel over one of our tiny buttons.</summary>
    static bool ScrollPrefix(Vector2 p, float steps, ref bool __result)
    {
        foreach (var t in s_tiny)
        {
            bool ours = false;
            Guard.Run(t.Id, "wheel", () => ours = t.Scroll(p, steps));
            if (ours) { __result = true; return false; }
        }
        return true;
    }
}

/// <summary>PZA UI rules 1: a throw from a window's code is logged (capped) and never reaches the game; a window that keeps throwing in its
/// frame or bind is switched off for the session.</summary>
public static class Guard
{
    static readonly Dictionary<string, int> s_faults = new(StringComparer.Ordinal);

    public static void Run(string who, string what, Action a)
    {
        try { a(); }
        catch (Exception e) { Fault(who, what, e); }
    }

    public static void Fault(string who, string what, Exception e)
    {
        int n = s_faults[who] = s_faults.GetValueOrDefault(who) + 1;
        if (n <= 5) PzaUi.Log($"PZA's Meitou UI: {who} {what} FAULT {n}: {e.GetType().Name}: {e.Message}");
        if (n == 50) PzaUi.Log($"PZA's Meitou UI: {who}: 50 faults, it stays off for this session");
    }

    public static bool Dead(string who) => s_faults.GetValueOrDefault(who) >= 50;
}

/// <summary>The game's non-public members, looked up once by name (MeitouClient 0.20.1, clone ae1a402).</summary>
public static class Access
{
    public static Type PlayerInterface = null!;
    public static MethodInfo Steer = null!, BindFaithfulScreen = null!, PointerDown = null!, PointerMove = null!, PointerUp = null!, UiScroll = null!;
    public static AccessTools.FieldRef<object, MyGuiScreenHost> FaithfulScreens = null!;
    public static FieldInfo Width = null!, Height = null!;
    public static PropertyInfo? UiEditing, TitleOpen;
    public static AccessTools.FieldRef<object, Meitou.Game.Host.WorldSession>? Session;
    public static MethodInfo? SelectedIds;

    public static List<string> ResolveAll()
    {
        var missing = new List<string>();
        var game = typeof(MyGuiScreenHost).Assembly;
        PlayerInterface = game.GetType("Meitou.Game.PlayerInterface")!;
        if (PlayerInterface is null) { missing.Add("type Meitou.Game.PlayerInterface"); return missing; }
        var v2 = typeof(Vector2);
        Steer = Need(AccessTools.Method(PlayerInterface, "Steer", [typeof(bool)]), "PlayerInterface.Steer(bool)", missing);
        PointerDown = Need(AccessTools.Method(PlayerInterface, "MouseDown", [typeof(MouseButton), v2, typeof(bool), typeof(bool), typeof(bool)]), "PlayerInterface.MouseDown", missing);
        PointerMove = Need(AccessTools.Method(PlayerInterface, "MouseMove", [v2]), "PlayerInterface.MouseMove", missing);
        PointerUp = Need(AccessTools.Method(PlayerInterface, "MouseUp", [typeof(MouseButton), v2, typeof(bool)]), "PlayerInterface.MouseUp", missing);
        UiScroll = Need(AccessTools.Method(PlayerInterface, "UiScroll", [v2, typeof(float)]), "PlayerInterface.UiScroll", missing);
        var router = game.GetType("Meitou.Game.Ui.ScreenRouter");
        if (router is null) missing.Add("type Meitou.Game.Ui.ScreenRouter");
        else BindFaithfulScreen = Need(AccessTools.Method(router, "BindFaithfulScreen", [typeof(string)]), "ScreenRouter.BindFaithfulScreen(string)", missing);
        var f = AccessTools.Field(PlayerInterface, "FaithfulScreens");
        if (f is null || f.FieldType != typeof(MyGuiScreenHost)) missing.Add("field PlayerInterface.FaithfulScreens");
        else FaithfulScreens = AccessTools.FieldRefAccess<MyGuiScreenHost>(PlayerInterface, "FaithfulScreens");
        Width = AccessTools.Field(PlayerInterface, "Width")!;
        Height = AccessTools.Field(PlayerInterface, "Height")!;
        if (Width?.FieldType != typeof(int) || Height?.FieldType != typeof(int)) missing.Add("fields PlayerInterface.Width / Height");
        UiEditing = AccessTools.Property(PlayerInterface, "UiEditing");
        SelectedIds = AccessTools.Method(PlayerInterface, "SelectedIds", Type.EmptyTypes);
        // optional (windows show everywhere without them): the title screen and the session, for InGame
        TitleOpen = AccessTools.Property(PlayerInterface, "TitleOpen") is { PropertyType: var tt } tp && tt == typeof(bool) ? tp : null;
        Session = AccessTools.Field(PlayerInterface, "Session")?.FieldType == typeof(Meitou.Game.Host.WorldSession)
            ? AccessTools.FieldRefAccess<Meitou.Game.Host.WorldSession>(PlayerInterface, "Session") : null;
        return missing;
    }

    static MethodInfo Need(MethodInfo? m, string name, List<string> missing)
    {
        if (m is null) missing.Add("method " + name);
        return m!;
    }
}
