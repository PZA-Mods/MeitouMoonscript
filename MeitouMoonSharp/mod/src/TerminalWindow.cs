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
    static string s_editorCode = DefaultEditorSnippet;
    static bool s_clearInput, s_syncEditor, s_wantFocus;
    static int s_scroll;
    static int s_lastMaxScroll;

    const string DefaultEditorSnippet =
        "-- Multi-line Lua Editor (click RUN CODE to execute)\n" +
        "local sq = world.squad()\n" +
        "print('Squad members: ' .. #sq)\n" +
        "for i, m in ipairs(sq) do\n" +
        "  print(string.format('  %d. %s (%s) HP %.0f/%.0f', i, m.name, m.weapon, m.hp, m.maxHp))\n" +
        "end";
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
        for (int i = 0; i < Rows; i++) w.Add(L.Text(RowName(i), 0.015, top + i * height / Rows, 0.90, height / Rows));
        // Compact right-rail scroll controls (PG UP, UP, DN, END) so long output is always readable
        w.Add(L.Button("btnPgUp", 0.922, 0.025, 0.068, 0.07));
        w.Add(L.Button("btnUp",   0.922, 0.105, 0.068, 0.07));
        w.Add(L.Button("btnDn",   0.922, 0.625, 0.068, 0.07));
        w.Add(L.Button("btnEnd",  0.922, 0.705, 0.068, 0.07));
        w.Add(L.Text("lbStatus", 0.015, 0.795, 0.97, 0.05));
        w.Add(L.Edit("edIn", 0.005, 0.875, 0.80, 0.09, maxLength: ReplCore.MaxInput));
        w.Add(L.Button("RunButton", 0.82, 0.875, 0.175, 0.09, isDefault: true));
        return [.. w];
    }

    static string[] EditorPage() =>
    [
        "<Widget type=\"EditBox\" skin=\"Kenshi_EditBox\" position_real=\"0.005 0.01 0.99 0.845\" name=\"edCode\">"
            + "<Property key=\"MultiLine\" value=\"true\"/>"
            + "<Property key=\"WordWrap\" value=\"true\"/>"
            + "<Property key=\"TextAlign\" value=\"Left Top\"/>"
            + $"<Property key=\"MaxTextLength\" value=\"{ReplCore.MaxEditorChars}\"/>"
            + "</Widget>",
        L.Button("btnRunCode", 0.005, 0.875, 0.34, 0.09),
        L.Button("btnDemoCode", 0.36, 0.875, 0.32, 0.09),
        L.Button("btnClearCode", 0.695, 0.875, 0.30, 0.09),
    ];

    static string LayoutXml() => L.Doc(L.WindowMin("Root", "MOONSCRIPT TERMINAL", 0.22, 0.12, 0.56, 0.7,
        L.Tabs("Tabs", 0.02, 0.02, 0.96, 0.96,
            ("TabTerm", TerminalPage()),
            L.HelpList("TabHelp", "lstHelp"),
            ("TabEdit", EditorPage()))));

    public static void Setup(HostSettings settings, Action<string, object> set, string folder)
    {
        s_folder = folder;
        Window = new PzaWindow(Id, LayoutXml)
        {
            Minimizable = true,
            Bind = Bind,
            Event = Event,
            OpenChanged = open => { s_wantFocus = open; if (open) s_syncEditor = true; },
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
        if (tab == 1)
        {
            // MyGuiScreenHost.PressChrome changes tabs without blurring _focus, leaving hidden edIn holding UiEditing / PzaUi.Typing
            s_wantFocus = false;
            if (host.Focus is { Screen: Id }) host.Blur();
            return;
        }
        if (tab == 2)
        {
            // EDITOR tab: blur hidden edIn if it still held focus, but let edCode keep keyboard focus while the user types
            s_wantFocus = false;
            if (host.Focus is { Screen: Id, Widget: "edIn" }) host.Blur();
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
        s_scroll = 0;
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
        PzaWindow.Enable(b, "btnPgUp");
        PzaWindow.Enable(b, "btnUp");
        PzaWindow.Enable(b, "btnDn");
        PzaWindow.Enable(b, "btnEnd");
        PzaWindow.Enable(b, "btnRunCode");
        PzaWindow.Enable(b, "btnDemoCode");
        PzaWindow.Enable(b, "btnClearCode");
        PzaWindow.TabText(b, "Tabs", 0, "TERMINAL");
        PzaWindow.TabText(b, "Tabs", 1, "HELP");
        PzaWindow.TabText(b, "Tabs", 2, "EDITOR");
        b.Text["RunButton"] = Session?.Core.Continuing == true ? ">>" : "RUN";
        b.Text["btnPgUp"] = "^^";
        b.Text["btnUp"] = "^";
        b.Text["btnDn"] = "v";
        b.Text["btnEnd"] = "END";
        b.Text["btnRunCode"] = "RUN CODE";
        b.Text["btnDemoCode"] = "DEMO SNIPPET";
        b.Text["btnClearCode"] = "CLEAR";
        PzaWindow.Tooltip(b, "btnPgUp", "Scroll up one page");
        PzaWindow.Tooltip(b, "btnUp", "Scroll up 4 lines");
        PzaWindow.Tooltip(b, "btnDn", "Scroll down 4 lines");
        PzaWindow.Tooltip(b, "btnEnd", "Jump to latest output");
        PzaWindow.Tooltip(b, "RunButton", "Run the line (Enter). Incomplete blocks automatically continue on the next line; /cancel clears.");
        if (s_clearInput) { b.Text["edIn"] = ""; s_clearInput = false; }
        if (s_syncEditor) { b.Text["edCode"] = s_editorCode; s_syncEditor = false; }
        if (Session is { } s)
        {
            var core = s.Core;
            var panelBounds = PzaUi.Host?.LastFrame.FirstOrDefault(f => f.Id == Id)?.Controls.FirstOrDefault(c => c.Name == "Panel")?.Bounds;
            int wrapCols = panelBounds is { Width: > 100 } pb ? Math.Clamp((int)(pb.Width * 0.90f / 8.2f), 48, 120) : 78;
            var wrapped = Wrap(core.Visible().ToList(), wrapCols);
            int maxScroll = Math.Max(0, wrapped.Count - Rows);
            s_lastMaxScroll = maxScroll;
            if (s_scroll > maxScroll) s_scroll = maxScroll;
            if (s_scroll < 0) s_scroll = 0;
            string scrollNote = s_scroll > 0 ? $"   |   scroll -{s_scroll}/{wrapped.Count} (END)" : "";
            b.Text["lbStatus"] = $"{core.Spinner}  {(core.Busy ? "typing" : core.Status)}   |   last {core.LastInstructions.ToString("N0", CultureInfo.InvariantCulture)} instr{scrollNote}   |   /help";
            PzaWindow.Props(b, "lbStatus", ("TextColour", Colour(core.Busy ? Ink.Accent : Ink.Dim)));
            var shown = Slice(wrapped, Rows, s_scroll);
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
        if (e.Widget == "edCode" && e.Action == "text") { s_editorCode = e.Text ?? ""; return; }
        if (e.Action == "RunButton" || e.Widget == "RunButton") { Submit(s_draft); return; }
        if (e.Action == "btnPgUp" || e.Widget == "btnPgUp") { s_scroll = Math.Min(s_lastMaxScroll, s_scroll + Math.Max(1, Rows - 2)); return; }
        if (e.Action == "btnUp" || e.Widget == "btnUp") { s_scroll = Math.Min(s_lastMaxScroll, s_scroll + 4); return; }
        if (e.Action == "btnDn" || e.Widget == "btnDn") { s_scroll = Math.Max(0, s_scroll - 4); return; }
        if (e.Action == "btnEnd" || e.Widget == "btnEnd") { s_scroll = 0; return; }
        if (e.Action == "btnRunCode" || e.Widget == "btnRunCode")
        {
            s_scroll = 0;
            Session?.Core.SubmitBlock(s_editorCode);
            try { PzaUi.Host?.SetTab(Id, "Tabs", 0); } catch (Exception) { }
            s_wantFocus = true;
            return;
        }
        if (e.Action == "btnDemoCode" || e.Widget == "btnDemoCode")
        {
            s_editorCode = DefaultEditorSnippet;
            s_syncEditor = true;
            try { PzaUi.Host?.SetText(Id, "edCode", s_editorCode); } catch (Exception) { }
            return;
        }
        if (e.Action == "btnClearCode" || e.Widget == "btnClearCode")
        {
            s_editorCode = "";
            s_syncEditor = true;
            try { PzaUi.Host?.SetText(Id, "edCode", ""); } catch (Exception) { }
            return;
        }
    }

    /// <summary>Word-wraps logical output lines to `maxCols` so long lines never clip off the right edge.</summary>
    public static List<(string Text, Ink Ink)> Wrap(IReadOnlyList<(string Text, Ink Ink)> lines, int maxCols)
    {
        maxCols = Math.Max(20, maxCols);
        var outLines = new List<(string Text, Ink Ink)>(lines.Count);
        foreach (var (raw, ink) in lines)
        {
            string s = raw ?? "";
            if (s.Length <= maxCols) { outLines.Add((s, ink)); continue; }
            int pos = 0;
            bool first = true;
            while (pos < s.Length)
            {
                int avail = first ? maxCols : Math.Max(16, maxCols - 2);
                int rem = s.Length - pos;
                if (rem <= avail)
                {
                    outLines.Add(((first ? "" : "  ") + s[pos..], ink));
                    break;
                }
                int cut = s.LastIndexOf(' ', pos + avail, Math.Min(avail, rem));
                if (cut <= pos + avail / 3) cut = pos + avail;
                string chunk = s[pos..cut].TrimEnd();
                outLines.Add(((first ? "" : "  ") + chunk, ink));
                pos = cut;
                while (pos < s.Length && s[pos] == ' ') pos++;
                first = false;
            }
        }
        return outLines;
    }

    /// <summary>Returns a `rows`-sized window into `lines`, offset `scrollFromBottom` lines above the end.</summary>
    public static List<(string Text, Ink Ink)> Slice(List<(string Text, Ink Ink)> lines, int rows, int scrollFromBottom)
    {
        if (lines.Count <= rows) return lines;
        int end = Math.Clamp(lines.Count - Math.Max(0, scrollFromBottom), rows, lines.Count);
        return lines.GetRange(end - rows, rows);
    }

    /// <summary>The newest `rows` lines (the scrollback shows its end; /clear empties it).</summary>
    public static List<(string Text, Ink Ink)> Tail(List<(string Text, Ink Ink)> lines, int rows) =>
        Slice(lines, rows, 0);
}
