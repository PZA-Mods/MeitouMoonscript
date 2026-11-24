// MeitouMoonscript. Author: PZA. Licence: GPLv3 or later.
// The in-game REPL window on PzaUi (a help page inside, F7 hides it with the HUD, the place is remembered, Shift+Y and the
// debug menu open it). Two tabs: TERMINAL (the scrollback with coloured lines, the status line with its spinner, the prompt, RUN) and HELP.
// The dark panel is an ImageBox showing ui/panel.png from the mod folder ("file:" textures, Meitou's UiPainter; a Meitou without them
// shows Kenshi's own page instead) and the scrollback is a column of plain text rows, each with its own TextColour (a Kenshi ListBox
// paints its own parchment over anything behind it: the first headless picture, 2026-11-22).
// Enter in the prompt presses RUN (the window's default button), so the box keeps the keyboard and the next line can be typed at once.
// While the prompt has the keyboard, Meitou's PlayerInterface.UiEditing is true: the game's keys, PZA's WASD & Controller Support and
// our own Shift+Y stand down, so typing never moves a character.
using System.Globalization;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using PZA.MeitouUi;
using PZA.Moonscript.Repl;
using L = PZA.MeitouUi.PzaLayout;

namespace PZA.Moonscript;

public static class ReplWindow
{
    public const string Id = "pza.moonscript.terminal";
    public static PzaWindow Window = null!;
    public static ReplSession? Session;
    static string s_draft = "";
    static bool s_clearInput, s_wantFocus;
    static MethodInfo? s_focusEdit;
    static bool s_focusEditLooked;
    static readonly Queue<string> s_demo = new();
    static int s_demoWait;
    public static long Submitted;

    public const int Rows = 22;
    static string s_folder = "";

    // the palette: our own (no copied branding). Meitou reads a TextColour as "#rrggbb" or "r g b" (UiDocument.cs; bare hex is ignored)
    public static string Colour(Ink ink) => ink switch
    {
        Ink.Banner => "#5FD7AF",
        Ink.Prompt => "#F0B45A",
        Ink.Result => "#9AE6FF",
        Ink.Error => "#FF6B6B",
        Ink.Dim => "#8A9099",
        Ink.Accent => "#C8A2FF",
        _ => "#E6E6E6",
    };

    static string RowName(int i) => "r" + i.ToString(CultureInfo.InvariantCulture);

    static string[] TerminalPage()
    {
        var w = new List<string>
        {
            "<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.005 0.01 0.99 0.845\" name=\"Panel\"/>",
            "<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.005 0.01 0.99 0.004\" name=\"Rule\"/>",
        };
        const double top = 0.022, height = 0.76;
        for (int i = 0; i < Rows; i++) w.Add(L.Text(RowName(i), 0.02, top + i * height / Rows, 0.96, height / Rows));
        w.Add(L.Text("lbStatus", 0.02, 0.795, 0.96, 0.05));
        w.Add(L.Edit("edIn", 0.005, 0.875, 0.80, 0.09, maxLength: ReplCore.MaxInput));
        w.Add(L.Button("RunButton", 0.82, 0.875, 0.175, 0.09, isDefault: true));
        return [.. w];
    }

    static string LayoutXml() => L.Doc(L.WindowMin("Root", "MOONSCRIPT TERMINAL", 0.22, 0.12, 0.56, 0.7,
        L.Tabs("Tabs", 0.02, 0.02, 0.96, 0.96,
            ("TabTerm", TerminalPage()),
            L.HelpList("TabHelp", "lstHelp"))));

    public static void Setup(HostSettings settings, Action<string, object> set, string folder)
    {
        s_folder = folder;
        Window = new PzaWindow(Id, LayoutXml)
        {
            Minimizable = true,
            Bind = Bind,
            Event = Event,
            OpenChanged = open => s_wantFocus = open,
            LoadPlace = () => new Vector2((float)settings.WindowPlaceX, (float)settings.WindowPlaceY),
            SavePlace = p => { set(nameof(HostSettings.WindowPlaceX), Math.Round((double)p.X, 3)); set(nameof(HostSettings.WindowPlaceY), Math.Round((double)p.Y, 3)); },
        };
        PzaUi.Add(Window);
    }

    public static void Toggle() => Window.Toggle();

    /// <summary>The debug menu's demo: open the window and type a few lines through the same path as Enter (the headless pictures).</summary>
    public static void Demo()
    {
        Window.Open();
        s_demo.Clear();
        foreach (var l in new[] { "/help", "print(\"hello from Lua, the hour is \" .. world.hour())", "selected.name()", "/scripts", "/run hello", "/config typing", "2^10 + math.random(1, 6)" })
            s_demo.Enqueue(l);
        s_demoWait = 20;
    }

    /// <summary>Every frame (PzaUi.EachFrame): the animation, the demo, focus hygiene on open / RUN / tab switch.</summary>
    public static void EachFrame(HostSettings settings)
    {
        SyncFocus();
        if (Session is null) return;
        var core = Session.Core;
        core.Animate = settings.Typing;
        core.CharsPerFrame = settings.TypingSpeed;
        core.Frame();
        if (s_demo.Count > 0 && Window.IsOpen && !core.Busy && --s_demoWait <= 0)
        {
            Submit(s_demo.Dequeue());
            s_demoWait = 25;
        }
    }

    static void SyncFocus()
    {
        if (PzaUi.Host is not { } host || !host.IsOpen(Id)) { s_wantFocus = false; return; }
        int tab = host.GetTab(Id, "Tabs");
        if (tab != 0)
        {
            // MyGuiScreenHost.PressChrome changes tabs without blurring _focus, leaving hidden edIn holding UiEditing / PzaUi.Typing
            s_wantFocus = false;
            if (host.Focus is { Screen: Id }) host.Blur();
            return;
        }
        if (!s_wantFocus) return;
        if (host.Focus is { Screen: Id, Widget: "edIn" }) { s_wantFocus = false; return; }
        if (host.OpenScreens.Count == 0 || host.OpenScreens[^1] != Id) return;
        var frame = host.LastFrame.FirstOrDefault(f => f.Id == Id);
        var edit = frame?.Controls.FirstOrDefault(c => c.Name == "edIn" && c.Type == "EditBox" && c.Enabled);
        if (edit is null) return;
        try
        {
            if (!s_focusEditLooked)
            {
                s_focusEditLooked = true;
                s_focusEdit = AccessTools.Method(typeof(MyGuiScreenHost), "FocusEdit", [typeof(string), typeof(UiControl)]);
            }
            if (s_focusEdit is not null) s_focusEdit.Invoke(host, [Id, edit]);
            else host.FocusNext(false);
            s_wantFocus = false;
        }
        catch (Exception)
        {
            s_wantFocus = false;
        }
    }

    static void Submit(string line)
    {
        Submitted++;
        Session?.Core.Submit(line);
        s_draft = "";
        s_clearInput = true;
        // the binding's text alone leaves the focused box's own buffer as it was (the next line would be typed after the old one: the
        // offline check found it); the host's SetText empties both
        try { PzaUi.Host?.SetText(Id, "edIn", ""); } catch (Exception) { }
        // clicking RunButton drops _focus in MyGuiScreenHost.PointerDown; restore focus to edIn on the next frame so typing keeps working
        if (Window.IsOpen && PzaUi.Host?.GetTab(Id, "Tabs") == 0) s_wantFocus = true;
    }

    static void Bind(UiBindings b)
    {
        PzaWindow.Enable(b, "RunButton");
        PzaWindow.TabText(b, "Tabs", 0, "TERMINAL");
        PzaWindow.TabText(b, "Tabs", 1, "HELP");
        b.Text["RunButton"] = "RUN";
        PzaWindow.Tooltip(b, "RunButton", "Run the line (Enter). Shift+Y opens and closes the terminal.");
        if (s_clearInput) { b.Text["edIn"] = ""; s_clearInput = false; }
        if (Session is { } s)
        {
            var core = s.Core;
            b.Text["lbStatus"] = $"{core.Spinner}  {(core.Busy ? "typing" : core.Status)}   |   last {core.LastInstructions.ToString("N0", CultureInfo.InvariantCulture)} instructions   |   Lua 5.2 sandbox   |   /help";
            PzaWindow.Props(b, "lbStatus", ("TextColour", Colour(core.Busy ? Ink.Accent : Ink.Dim)));
            var shown = Tail(core.Visible().ToList(), Rows);
            for (int i = 0; i < Rows; i++)
            {
                var (text, ink) = i < shown.Count ? shown[i] : ("", Ink.Text);
                b.Text[RowName(i)] = text.Length == 0 ? " " : text;
                PzaWindow.Props(b, RowName(i), ("TextColour", Colour(ink)));
            }
        }
        if (s_folder.Length > 0)
        {
            PzaWindow.Props(b, "Panel", ("ImageTexture", "file:" + Path.Combine(s_folder, "ui", "panel.png")));
            PzaWindow.Props(b, "Rule", ("ImageTexture", "file:" + Path.Combine(s_folder, "ui", "rule.png")));
        }
        Window.Paragraphs(b, "lstHelp", Help);
    }

    const string Help =
        "MeitouMoonscript by PZA: an in-game Lua console. Type a line and press Enter (or RUN). A line that starts with / is a command; anything else is Lua.\n\n" +
        "Commands: /help, /run <script> (runs scripts/<script>.lua from this mod's folder, read again every time, so you can edit it while the game runs), /reload (the last script again), /scripts, /config [words] (the settings, filtered by the words you type; /config set <key> <value> changes one), /reset (a fresh Lua state), /clear.\n\n" +
        "What Lua can do here: print(...), world.hour(), world.day(), world.cats(), world.squad() (up to 8 characters from your selection and active squad: stats, HP, race, weapon, armour), selected.name(), selected.info(), and Lua's own string, table, math (math.random is seeded, never the clock), bit32 and coroutine libraries. Try /run battle for a Kenshi auto-battler using your live squad. It cannot open files, read the clock, load code or touch the world.\n\n" +
        "Every line runs under a budget (Settings > Mods > Terminal budget per call, default 200 thousand instructions) and caps on memory and depth: a line that passes one is stopped and the terminal's Lua state starts fresh. Nothing you type here is saved with the game.\n\n" +
        "While you type in the prompt, the game's keys stand down (Meitou's own, PZA's WASD & Controller Support, Shift+Y): typing never moves a character. Shift+Y opens and closes the terminal; F7 hides it with the HUD; drag the title to move it.";

    static void Event(MyGuiEvent e)
    {
        if (e.Widget == "edIn" && e.Action == "text") { s_draft = e.Text ?? ""; return; }
        if (e.Action == "RunButton" || e.Widget == "RunButton") Submit(s_draft);
    }

    /// <summary>The newest `rows` lines (the scrollback shows its end; /clear empties it).</summary>
    public static List<(string Text, Ink Ink)> Tail(List<(string Text, Ink Ink)> lines, int rows) =>
        lines.Count <= rows ? lines : lines.GetRange(lines.Count - rows, rows);
}
