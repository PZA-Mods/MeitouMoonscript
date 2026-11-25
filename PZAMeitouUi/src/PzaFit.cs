// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// Text that fits its widget (the user, 2026-11-17: "our menus are not fitting into their windows (the text)"). Meitou draws in-game text at
// least 16 px high at 1080 lines (Kenshi's own font is 13.9), so layouts made for Kenshi's size spill. Three parts:
//   - Paragraphs: long text (help pages) goes into a scrolling list, wrapped by the real font at the list's real width (PzaFont).
//   - The fit pass (after every bind): a one-line label or button caption still too wide wraps when its box has room for two lines, else is
//     cut with "..." and the whole text goes into its tooltip. It logs the first time it fires for a widget (a rule that never logs is untested).
//   - PzaFitCheck (check\PzaFitCheck.cs, in every mod's offline check): every string of every window in en / ru / zh / ja at three screen
//     sizes against its widget, with the fit pass off, so a layout that only fits by being cut fails there.
using System.Globalization;
using System.Reflection;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace PZA.MeitouUi;

public static class PzaFit
{
    /// <summary>The fit pass on (the offline check turns it off to see the raw layout).</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>The standard font resource every PZA text uses unless its skin names another.</summary>
    public const string StandardFont = "Kenshi_StandardFont_Medium";

    /// <summary>Where fonts come from when the host's resources are not readable: a Kenshi install and a language (the offline check sets them).</summary>
    public static string? KenshiDir { get; set; }
    public static string Lang { get; set; } = "en";

    static FieldInfo? s_resField;
    static readonly HashSet<string> s_logged = new(StringComparer.Ordinal);

    /// <summary>The MyGUI resources a host was made with (its constructor's argument).</summary>
    public static MyGuiResources? Resources(MyGuiScreenHost host)
    {
        s_resField ??= typeof(MyGuiScreenHost).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .FirstOrDefault(f => f.FieldType == typeof(MyGuiResources));
        return s_resField?.GetValue(host) as MyGuiResources;
    }

    /// <summary>A font resource at a viewport height: from the Kenshi install when <see cref="KenshiDir"/> is set, else as the game loaded it.</summary>
    public static PzaFont? Font(MyGuiResources? res, string fontName, int viewportHeight) =>
        KenshiDir is { } dir ? PzaFont.FromInstall(dir, Lang, fontName, viewportHeight)
        : res is not null ? PzaFont.FromLoaded(res.LoadedFiles, fontName, viewportHeight) : null;

    /// <summary>The font a control's text is drawn in: its FontName, else its skin template's, else the standard font.</summary>
    public static string FontName(MyGuiResources? res, UiControl c) =>
        c.Properties.GetValueOrDefault("FontName")
        ?? (res is not null && res.Templates.TryGetValue(c.Skin, out var t)
            ? t.Descendants("Property").FirstOrDefault(p => (string?)p.Attribute("key") == "FontName")?.Attribute("value")?.Value : null)
        ?? StandardFont;

    /// <summary>Where a control's text is laid out (its skin's text part less padding); null when the skin has no text.</summary>
    public static UiRect? TextRect(MyGuiResources? res, UiControl c, int viewportHeight)
    {
        if (res is null) return c.Bounds;
        if (!res.Skins.TryGetValue(c.Skin, out var skin) || !skin.Parts.Any(p => p.Type is "SimpleText" or "EditText")) return null;
        return ContentRect(res, c);   // the skin's text part aligned in the bounds, less TextPadding
    }

    /// <summary>UiTextLayout.ContentRect through whichever overload the RUNNING Meitou has. 0.3.2: Meitou 0.21+ added an optional `scale`
    /// parameter, so the 0.20.1 two-argument call threw MissingMethodException there (every bind of a window with text fitting faulted; after 50
    /// the window stayed off). The optional parameters get their defaults.</summary>
    static UiRect ContentRect(MyGuiResources res, UiControl c)
    {
        s_contentRect ??= typeof(UiTextLayout).GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name == "ContentRect" && m.ReturnType == typeof(UiRect))
            .Where(m => { var p = m.GetParameters(); return p.Length >= 2 && p[0].ParameterType == typeof(MyGuiResources)
                && p[1].ParameterType == typeof(UiControl) && p.Skip(2).All(x => x.IsOptional); })
            .OrderBy(m => m.GetParameters().Length).First();
        var ps = s_contentRect.GetParameters();
        var args = new object?[ps.Length];
        args[0] = res; args[1] = c;
        for (int i = 2; i < ps.Length; i++) args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
        return (UiRect)s_contentRect.Invoke(null, args)!;
    }

    static System.Reflection.MethodInfo? s_contentRect;

    /// <summary>Wraps paragraphs into list rows that fit <paramref name="list"/>'s rows (as last laid out) in the real font; before the first layout,
    /// at about <paramref name="fallbackChars"/> characters. Blank lines between paragraphs stay.</summary>
    public static List<string> Paragraphs(PzaWindow w, string list, string text, int fallbackChars = 50)
    {
        if (w.RowWidth(list) is { } width && w.Host is { } host && Font(Resources(host), StandardFont, w.LastHeight) is { } font)
            return font.Wrap(text, width - 2);
        return CharWrap(text, fallbackChars);
    }

    /// <summary>Whether a line fits a list's rows, in the real font at the list's real width (as last laid out); null before the list was drawn
    /// (wrap by characters until then). For a mod that wraps its own way (colours, indents): cut where this says no.</summary>
    public static Func<string, bool>? RowFits(PzaWindow w, string list)
    {
        if (w.RowWidth(list) is not { } width || w.Host is not { } host || Font(Resources(host), StandardFont, w.LastHeight) is not { } font) return null;
        return line => font.Width(line) <= width - 2;
    }

    /// <summary>A plain character-count wrap (before a font is known).</summary>
    public static List<string> CharWrap(string text, int width)
    {
        var rows = new List<string>();
        foreach (var para in text.Replace("\\n", "\n").Replace("\r", "").Split('\n'))
        {
            var line = new System.Text.StringBuilder();
            foreach (var word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width) { rows.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(word);
            }
            rows.Add(line.ToString());
        }
        return rows;
    }

    /// <summary>Punctuation a language's font may lack (the ru and ja fonts' Codes have no dashes or guillemets: a box in Meitou's faithful look)
    /// and the plain character drawn instead.</summary>
    static readonly (char From, string To)[] Plain =
        [('—', "-"), ('–', "-"), ('«', "\""), ('»', "\""), ('“', "\""), ('”', "\""), ('‘', "'"), ('’', "'"), ('…', "...")];

    /// <summary>Replaces punctuation the window's font cannot draw with a plain one (always on, also in the offline check).</summary>
    internal static void Substitute(PzaWindow w, UiBindings b, MyGuiScreenHost host)
    {
        if (Font(Resources(host), StandardFont, w.LastHeight) is not { } font) return;
        var missing = Plain.Where(p => !font.Draws(p.From)).ToArray();
        if (missing.Length == 0) return;
        string Fix(string t)
        {
            foreach (var (from, to) in missing) if (t.IndexOf(from) >= 0) t = t.Replace(from.ToString(), to);
            return t;
        }
        foreach (var (name, text) in b.Text.ToArray()) { string f = Fix(text); if (!ReferenceEquals(f, text) && f != text) b.Text[name] = f; }
        foreach (var (list, items) in b.Lists.ToArray())
            if (items.Any(i => missing.Any(m => i.Caption.IndexOf(m.From) >= 0)))
                b.Lists[list] = items.Select(i => i with { Caption = Fix(i.Caption) }).ToList();
    }

    /// <summary>The fit pass over one window's bound texts (PzaWindow.ApplyBind calls it after the mod's Bind).</summary>
    internal static void Apply(PzaWindow w, UiBindings b, MyGuiScreenHost host)
    {
        if (!Enabled || b.Text.Count == 0) return;
        var frame = host.LastFrame.FirstOrDefault(f => f.Id == w.Id);
        if (frame is null) return;
        var res = Resources(host);
        int h = w.LastHeight;
        foreach (var (name, text) in b.Text.ToArray())
        {
            if (string.IsNullOrEmpty(text) || name.Contains('@')) continue;
            var c = frame.Controls.FirstOrDefault(x => x.Name == name);
            if (c is null || c.Type == "EditBox" || TextRect(res, c, h) is not { Width: > 4 } r) continue;
            if (Font(res, FontName(res, c), h) is not { } font) return;
            bool wrap = c.Properties.GetValueOrDefault("WordWrap") == "true"
                || b.Properties.TryGetValue(name, out var p) && p.GetValueOrDefault("WordWrap") == "true";
            if (wrap)
            {
                var lines = font.Wrap(text, r.Width);
                int fit = Math.Max(1, (int)(r.Height / font.LineHeight));
                if (lines.Count <= fit) continue;
                b.Text[name] = string.Join("\n", lines.Take(fit - 1).Append(font.Ellipsize(lines[fit - 1], r.Width)));
                PzaWindow.Tooltip(b, name, text);
                Once(w, name, $"wrapped text {lines.Count} lines in a box for {fit}: cut, whole text in the tooltip");
                continue;
            }
            if (font.WidestLine(text) <= r.Width) continue;
            if (r.Height >= 2 * font.LineHeight && c.Type == "TextBox")
            {
                PzaWindow.Props(b, name, ("WordWrap", "true"), ("TextAlign", "Left Top"));
                Once(w, name, "one-line text too wide: wrapped (its box has room for two lines)");
                continue;
            }
            b.Text[name] = string.Join("\n", text.Split('\n').Select(l => font.Ellipsize(l, r.Width)));
            if (!(b.Properties.TryGetValue(name, out var tp) && tp.ContainsKey("tooltip_text"))) PzaWindow.Tooltip(b, name, text);
            Once(w, name, $"text {font.WidestLine(text):0} px in {r.Width:0} px: cut with '...', whole text in the tooltip");
        }
    }

    /// <summary>Meitou's row padding in a list without a line skin: TextPadding "4 2 4 2" (UiDocument.Widgets ListRows), 2 px above and below.</summary>
    const float RowPadding = 4;

    /// <summary>List rows as tall as the text Meitou draws in them. A ListBox row is the skin's HeightLine (Kenshi's size) unless the binding sets
    /// "LineHeight" (pixels; Meitou's own jobs list does), but Meitou draws its text taller than Kenshi's, so every PZA list clipped the
    /// tails of g / y / p and wrapped help lines overlapped (seen at 4K, 2026-11-18: "squad, by" drawn as "souad. bv"). A list whose
    /// binding already sets LineHeight keeps it.</summary>
    internal static void ListRows(PzaWindow w, UiBindings b, MyGuiScreenHost host)
    {
        if (b.Lists.Count == 0) return;
        var font = Font(Resources(host), StandardFont, w.LastHeight);
        if (font is null) return;
        string px = Px(MathF.Ceiling(font.LineHeight + RowPadding));
        foreach (var list in b.Lists.Keys.ToArray())
        {
            if (b.Properties.TryGetValue(list, out var p) && p.ContainsKey("LineHeight")) continue;
            PzaWindow.Props(b, list, ("LineHeight", px));
            Once(w, list, $"list rows {px} px (font line {Px(font.LineHeight)} px)");
        }
    }

    static void Once(PzaWindow w, string name, string what)
    {
        if (s_logged.Add(w.Id + "/" + name + "/" + Lang)) PzaUi.Log($"PZA's Meitou UI: fit '{w.Id}' {name}: {what}");
    }

    internal static string Px(float v) => v.ToString("0", CultureInfo.InvariantCulture);
}
