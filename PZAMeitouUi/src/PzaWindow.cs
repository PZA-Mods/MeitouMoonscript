// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// One real Kenshi (MyGUI) window on Meitou's MyGuiScreenHost: the layout (PzaLayout builds it), the mod's bind and event callbacks, open /
// close / toggle (queued; applied on the next frame on the host thread), and its place remembered as a FRACTION of the screen (so it survives a
// resolution change) through the mod's own load / save callbacks (its settings). What the host gives for free (Confirmed in game 2026-11-16):
// title-bar drag, raise, Escape, F7 with the HUD, keyboard focus and typing, tabs, lists, sliders, tick boxes, native tooltips; not modal = no pause.
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Host.Input;
using Meitou.Game.Ui;

namespace PZA.MeitouUi;

public sealed class PzaWindow(string id, Func<string> layoutXml, bool modal = false)
{
    /// <summary>The screen id, namespaced "pza.&lt;mod&gt;[.&lt;window&gt;]".</summary>
    public string Id { get; } = id;
    public bool Modal { get; } = modal;

    /// <summary>Fills the window's bindings every frame while it is open (texts, enabled buttons, ticks, lists, tooltips; see the helpers below).
    /// The game has already disabled every Button it does not know: <see cref="Enable"/> ours.</summary>
    public Action<UiBindings>? Bind { get; set; }
    /// <summary>Every event of this window: a button's action (its name, set by Enable), select / accept (lists, Value = line), text (edit
    /// boxes), scroll (sliders, Value = position), step.</summary>
    public Action<MyGuiEvent>? Event { get; set; }
    /// <summary>The saved place (the offset from the layout position as a fraction of the screen), or null for the layout's own place.</summary>
    public Func<Vector2?>? LoadPlace { get; set; }
    /// <summary>Called when the player has moved the window (after the drag ends) and at close, with the new place as a fraction of the screen.</summary>
    public Action<Vector2>? SavePlace { get; set; }
    /// <summary>Opened / closed (by us, by its close chrome, by Escape): the mod can follow (its settings, its hotkey's state).</summary>
    public Action<bool>? OpenChanged { get; set; }
    /// <summary>The window can be minimized (PZA UI rules 6 and 11): its layout uses PzaLayout.WindowMin (Kenshi's window with a minimize
    /// button, Kenshi_WindowCXM), and the "-" button or a double click on the title closes it and calls <see cref="Minimized"/> (the mod's tiny
    /// button shows while the window is closed, and its "+" opens it again).</summary>
    public bool Minimizable { get; init; }
    /// <summary>Called after the window was minimized ("-" or a title double click).</summary>
    public Action? Minimized { get; set; }

    /// <summary>The minimize button's action (bound to the template's "minimize" event, which Meitou does not handle itself).</summary>
    public const string MinimizeAction = "pza:minimize";

    MyGuiScreenHost? _host;
    string _root = "Root";
    bool _wantOpen, _wantClose, _wasOpen;
    long _titleT;
    Vector2 _titleAt;
    Vector2 _lastOffset;
    int _binds;

    /// <summary>The screen height of the last frame (fonts are sized by it).</summary>
    public int LastHeight { get; private set; } = 1080;

    /// <summary>The width of a list's rows' text as last laid out (null before the list was drawn, or with no rows).</summary>
    public float? RowWidth(string list)
    {
        var frame = _host?.LastFrame.FirstOrDefault(f => f.Id == Id);
        var row = frame?.Controls.FirstOrDefault(c => c.Name.StartsWith(list + "@row", StringComparison.Ordinal));
        if (row is null) return null;
        return PzaFit.TextRect(PzaFit.Resources(_host!), row, LastHeight)?.Width ?? row.Bounds.Width;
    }

    /// <summary>The host the window was last framed on.</summary>
    internal MyGuiScreenHost? Host => _host;

    public bool IsOpen => _host is not null && _host.IsRegistered(Id) && _host.IsOpen(Id);
    public void Open() { _wantOpen = true; _wantClose = false; }
    public void Close() { _wantClose = true; _wantOpen = false; }
    public void Toggle() { if (IsOpen || _wantOpen) Close(); else Open(); }

    /// <summary>One frame (PzaUi.Frame calls it).</summary>
    public void Frame(MyGuiScreenHost host, int width, int height)
    {
        if (Guard.Dead(Id)) return;
        if (height > 0) LastHeight = height;
        if (!ReferenceEquals(host, _host))
        {
            _host = host;   // a new host (a new session's interface): register again
            if (!host.IsRegistered(Id))
            {
                var layout = MyGuiReader.ParseLayout(layoutXml());
                host.Register(Id, layout, Modal);
                host.Activated += OnEvent;
                PzaUi.Log($"PZA's Meitou UI: window '{Id}' registered ({layout.Widgets.Count} top widgets{(Modal ? ", modal" : "")}{(Minimizable ? ", minimizable" : "")})");
            }
            if (host.Layout(Id).Widgets.FirstOrDefault(w => w.Type == "Window") is { } rootWidget) _root = rootWidget.Name;
        }

        // not in a game (the title screen, before a game loads): hidden; one that is or wants to be open comes back once a game runs.
        // Closed quietly: OpenChanged is not raised (the mod keeps its remembered form) and the place is not saved.
        if (!PzaUi.InGame)
        {
            if (host.IsOpen(Id))
            {
                host.Close(Id);
                _wantOpen = !_wantClose;
                PzaUi.Log($"PZA's Meitou UI: '{Id}' hidden until a game runs");
            }
            if (_wantClose) _wantOpen = _wantClose = false;
            return;
        }

        if (_wantOpen)
        {
            _wantOpen = false;
            if (!host.IsOpen(Id))
            {
                if (LoadPlace?.Invoke() is { } frac && float.IsFinite(frac.X) && float.IsFinite(frac.Y))
                    host.SetOffset(Id, new Vector2(frac.X * width, frac.Y * height));
                host.Open(Id);
                PzaUi.Log($"PZA's Meitou UI: '{Id}' opened at offset {F(host.GetOffset(Id))}");
            }
        }
        if (_wantClose)
        {
            _wantClose = false;
            if (host.IsOpen(Id)) host.Close(Id);
        }

        ApplyBars();
        bool open = host.IsOpen(Id);
        if (open && !host.CapturesPointer)
        {
            var off = host.GetOffset(Id);
            if (off != _lastOffset)
            {
                _lastOffset = off;
                if (_wasOpen) Save(off, width, height, "moved");   // not the jump an open makes
            }
        }
        if (open != _wasOpen)
        {
            if (!open) Save(host.GetOffset(Id), width, height, "closed");
            else _lastOffset = host.GetOffset(Id);
            _wasOpen = open;
            OpenChanged?.Invoke(open);
            if (!open) PzaUi.Log($"PZA's Meitou UI: '{Id}' closed");
        }
    }

    void Save(Vector2 off, int width, int height, string why)
    {
        if (SavePlace is null || width <= 0 || height <= 0) return;
        SavePlace(new Vector2(off.X / width, off.Y / height));
    }

    /// <summary>The bind call (the library's ScreenRouter.BindFaithfulScreen postfix calls it; public for offline checks).</summary>
    public void ApplyBind(UiBindings b)
    {
        if (_binds++ == 0) PzaUi.Log($"PZA's Meitou UI: '{Id}' first bind ({b.Disabled.Count} widget(s) arrived disabled)");
        // the minimize button is a template part whose event goes to its owner: "<root>@minimize" names the action it raises
        if (Minimizable) b.Actions[_root + "@minimize"] = MinimizeAction;
        Bind?.Invoke(b);
        if (_host is { } host) { PzaFit.Substitute(this, b, host); PzaFit.Apply(this, b, host); PzaFit.ListRows(this, b, host); }
    }

    void OnEvent(MyGuiEvent e)
    {
        if (e.Screen != Id || Guard.Dead(Id)) return;
        if (e.Action == MinimizeAction) { Minimize("the - button"); return; }
        Guard.Run(Id, "event " + e.Action, () => Event?.Invoke(e));
    }

    /// <summary>Moves a slider (or any scroll bar) to a position from code, e.g. when the value changed by a hotkey. The engine keeps the
    /// position itself after the player moves the thumb (it overwrites the binding's ScrollOffsets from its own state), so this sets the engine's
    /// state: host.SetScroll under the bar's "BarKey" marker, read from the last layout. While the window is closed it waits and is applied
    /// when the bar is laid out again. Moving it this way raises no scroll event.</summary>
    public void SetSlider(string bar, float position)
    {
        _pendingBars[bar] = position;
        ApplyBars();
    }

    readonly Dictionary<string, float> _pendingBars = new(StringComparer.Ordinal);

    void ApplyBars()
    {
        if (_pendingBars.Count == 0 || _host is not { } host || !IsOpen) return;
        var frame = host.LastFrame.FirstOrDefault(f => f.Id == Id);
        if (frame is null) return;
        foreach (var (bar, pos) in _pendingBars.ToArray())
        {
            var c = frame.Controls.FirstOrDefault(x => x.Name == bar && x.Properties.ContainsKey("BarKey"));
            if (c is null) continue;   // not laid out yet (another tab, or the first frame after an open): keep it
            host.SetScroll(Id, c.Properties["BarKey"], pos);
            _pendingBars.Remove(bar);
        }
    }

    /// <summary>Closes the window as a minimize (the mod's tiny button takes over).</summary>
    public void Minimize(string from)
    {
        Close();
        PzaUi.Log($"PZA's Meitou UI: '{Id}' minimized ({from})");
        Guard.Run(Id, "minimized", () => Minimized?.Invoke());
    }

    /// <summary>A left press anywhere (PlayerInterface.MouseDown prefix): two presses on the title strip within 400 ms and 6 px minimize the
    /// window (a drag never counts: its press is single). The strip is the header part of Kenshi's window template as last laid out; a press
    /// on its own buttons (close, minimize) does not count.</summary>
    public void PointerDown(MouseButton button, Vector2 p)
    {
        if (!Minimizable || button != MouseButton.Left || _host is not { } host || !IsOpen) return;
        var frame = host.LastFrame.FirstOrDefault(f => f.Id == Id);
        var header = frame?.Controls.FirstOrDefault(c => c.Skin == "Kenshi_GenericWindowHeaderSkin");
        if (header is null) return;
        var r = header.Bounds;
        if (p.X < r.X || p.Y < r.Y || p.X >= r.X + r.Width || p.Y >= r.Y + r.Height) return;
        if (host.HitTest(p) is { } hit && (hit.Screen != Id || hit.Control.Type == "Button")) return;
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        bool dbl = System.Diagnostics.Stopwatch.GetElapsedTime(_titleT, now).TotalMilliseconds < 400 && Vector2.Distance(p, _titleAt) < 6;
        _titleT = dbl ? 0 : now; _titleAt = p;
        if (dbl) Minimize("a double click on the title");
    }

    static string F(Vector2 v) => v.X.ToString("0", CultureInfo.InvariantCulture) + "," + v.Y.ToString("0", CultureInfo.InvariantCulture);

    // ---- binding helpers (each found the hard way, in PZA's UI Probe) ----

    /// <summary>Enables buttons the game disabled and names their action (the event's Action = the name).</summary>
    public static void Enable(UiBindings b, params string[] names)
    {
        foreach (var n in names) { b.Disabled.Remove(n); b.Actions[n] = n; }
    }

    /// <summary>A tick box's state (the game's own form uses the same two properties).</summary>
    public static void Tick(UiBindings b, string name, bool on) =>
        Props(b, name, ("StateSelected", on ? "true" : "false"), ("Checked", on ? "true" : "false"));

    /// <summary>A native Kenshi tooltip on a widget. Through the binding: a layout's UserStrings do not reach the control.</summary>
    public static void Tooltip(UiBindings b, string name, string text) => Props(b, name, ("tooltip_type", "0"), ("tooltip_text", text));

    /// <summary>A tab's caption. Through Text["tabs@tabN"]: binding TabCaptions instead puts the control in one-page mode.</summary>
    public static void TabText(UiBindings b, string tabs, int index, string text) => b.Text[tabs + "@tab" + index.ToString(CultureInfo.InvariantCulture)] = text;

    /// <summary>Adds properties to a widget's binding, keeping what is already there (a tick and a tooltip on one button, say).</summary>
    public static void Props(UiBindings b, string name, params (string Key, string Value)[] props)
    {
        var merged = b.Properties.TryGetValue(name, out var old) ? new Dictionary<string, string>(old, StringComparer.Ordinal) : new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in props) merged[k] = v;
        b.Properties[name] = merged;
    }

    /// <summary>Long text into a scrolling list (PzaLayout.HelpList / TextList): its paragraphs wrapped at the list's real row width in the real
    /// font of the player's language (PzaFont), a blank row between paragraphs kept. Before the list was first drawn, about 50 characters a row.</summary>
    public void Paragraphs(UiBindings b, string list, string text) => Lines(b, list, PzaFit.Paragraphs(this, list, text));

    /// <summary>A list's lines (ids "0".."n-1") and its selected line (-1 = none).</summary>
    public static void Lines(UiBindings b, string list, IEnumerable<string> lines, int selected = -1)
    {
        b.Lists[list] = lines.Select((s, i) => Item(i.ToString(CultureInfo.InvariantCulture), s)).ToList();
        if (selected >= 0) b.SelectedItems[list] = selected;
    }

    /// <summary>A list row through whichever constructor the RUNNING Meitou has. 0.3.2: Meitou 0.21 gave UiListItem a fifth field (Colour), so
    /// `new UiListItem(id, caption)` compiled against 0.20.1 (its four-field constructor, the optional ones filled in) threw MissingMethodException
    /// on 0.21+: every window with a list (the Help pages) faulted 50 times and stayed off for the session (headless run on 0.22.1, 2026-11-19).
    /// Here the constructor is looked up once at run time and its optional fields get their defaults.</summary>
    public static UiListItem Item(string id, string caption)
    {
        s_itemCtor ??= typeof(UiListItem).GetConstructors()
            .Where(c => { var p = c.GetParameters(); return p.Length >= 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType == typeof(string)
                && p.Skip(2).All(x => x.IsOptional); })
            .OrderBy(c => c.GetParameters().Length).First();
        var ps = s_itemCtor.GetParameters();
        var args = new object?[ps.Length];
        args[0] = id; args[1] = caption;
        for (int i = 2; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        return (UiListItem)s_itemCtor.Invoke(args);
    }

    static System.Reflection.ConstructorInfo? s_itemCtor;
}

/// <summary>Changing a mod setting from code through its ModSettingsGroup (the Settings page shows the new value), falling back to the property when
/// the mod has no group yet. 0.3.1: Meitou keeps a mod value in meitou.user.json ONLY when the player changes it on the Settings page
/// (ModSettingsRows.KeepModSetting); ModSetting.Set from the mod only sets the property and raises Changed. So a window drag, a slider or a tiny-button
/// move was lost at every restart (the user, 2026-11-17: "SlowMo ALWAYS open front and center at 0.50x"). <see cref="Keep"/> fixes that: the group's
/// values go to "&lt;mod id&gt;.settings.json" in the mod's own folder on EVERY Changed (the page's and the mod's alike, so the file always holds the
/// latest value), and are put back right after AddSettings (after Meitou's own saved values).</summary>
public static class PzaSettings
{
    public static void Set(Meitou.Modding.ModSettingsGroup? group, object target, string key, object value)
    {
        var s = group?.Settings.FirstOrDefault(x => x.Key == key);
        if (s is not null) s.Set(value);
        else target.GetType().GetProperty(key)!.SetValue(target, value);
    }

    /// <summary>The file a mod's kept values live in.</summary>
    public static string FileFor(string folder, string modId) => Path.Combine(folder, modId + ".settings.json");

    /// <summary>Call once, right after context.AddSettings (before the mod reads its settings or subscribes to Changed): puts the kept values
    /// back, then keeps every later change. Returns lines for the mod's log. Never throws.</summary>
    public static List<string> Keep(Meitou.Modding.ModSettingsGroup group, string folder)
    {
        var lines = new List<string>();
        string path = FileFor(folder, group.ModId), name = Path.GetFileName(path);
        int applied = 0;
        var refused = new List<string>();
        try
        {
            if (File.Exists(path))
            {
                var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? [];
                foreach (var (key, text) in saved)
                {
                    var s = group.Settings.FirstOrDefault(x => x.Key == key);
                    object? v = s is null ? null : Parse(s, text);
                    if (v is null) { refused.Add(key); continue; }
                    if (s!.Text == text) continue;
                    try { s.Set(v); applied++; }
                    catch { refused.Add(key); }
                }
            }
            lines.Add($"PZA's Meitou UI: settings kept in {name}: {applied} value(s) put back" + (refused.Count > 0 ? $"; ignored {string.Join(", ", refused)}" : ""));
        }
        catch (Exception e)
        {
            lines.Add($"PZA's Meitou UI: could not read {name} ({e.GetType().Name}: {e.Message}); starting from Meitou's saved settings");
        }
        bool failed = false;
        group.Changed += _ =>
        {
            try { Save(group, path); }
            catch (Exception e)
            {
                if (failed) return;
                failed = true;
                PzaUi.Log($"PZA's Meitou UI: could not write {name} ({e.GetType().Name}: {e.Message}); changes made in the game are not kept");
            }
        };
        return lines;
    }

    /// <summary>Every setting's current value (invariant text, as Meitou's own file) to the file; written to a temporary file first.</summary>
    public static void Save(Meitou.Modding.ModSettingsGroup group, string path)
    {
        var all = group.Settings.ToDictionary(s => s.Key, s => s.Text, StringComparer.Ordinal);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, System.Text.Json.JsonSerializer.Serialize(all, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, overwrite: true);
    }

    static object? Parse(Meitou.Modding.ModSetting s, string text) => s.Kind switch
    {
        Meitou.Modding.ModSettingKind.Switch => bool.TryParse(text, out bool b) ? b : null,
        Meitou.Modding.ModSettingKind.Slider => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && double.IsFinite(d) ? d : null,
        _ => s.Options.Contains(text) ? text : null,
    };
}
