// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// Builds MyGUI layout XML with Kenshi's own skins, so a PZA window is described in code, not hand-written XML. Positions are fractions of the
// parent (position_real), so windows scale with the screen (PZA UI rules 7). Names must be unique within a window. Every skin used here was
// checked in Kenshi's install (PZA's UI Probe offline check) and drawn in game (2026-11-16).
//
//   PzaLayout.Doc(PzaLayout.Window("Root", "MY WINDOW", 0.3, 0.2, 0.36, 0.5,
//       PzaLayout.Text("lbIntro", 0.03, 0.03, 0.94, 0.12, wrap: true),
//       PzaLayout.Button("CloseButton", 0.76, 0.87, 0.21, 0.07, isDefault: true)))
using System.Globalization;
using System.Security;
using System.Text;

namespace PZA.MeitouUi;

public static class PzaLayout
{
    static string F(double v) => v.ToString("0.#####", CultureInfo.InvariantCulture);
    static string Pos(double x, double y, double w, double h) => $"position_real=\"{F(x)} {F(y)} {F(w)} {F(h)}\"";
    static string Esc(string s) => SecurityElement.Escape(s) ?? "";
    static string Prop(string k, string v) => $"<Property key=\"{Esc(k)}\" value=\"{Esc(v)}\"/>";

    /// <summary>The whole layout.</summary>
    public static string Doc(params string[] roots) => "<MyGUI type=\"Layout\">" + string.Concat(roots) + "</MyGUI>";

    /// <summary>A Kenshi window (Kenshi_WindowCX: caption, close chrome, title-bar drag), placed as a fraction of the screen.</summary>
    public static string Window(string name, string caption, double x, double y, double w, double h, params string[] children) =>
        PagesBelowStrip($"<Widget type=\"Window\" skin=\"Kenshi_WindowCX\" {Pos(x, y, w, h)} layer=\"Window\" name=\"{Esc(name)}\">{Prop("Caption", caption)}{string.Concat(children)}</Widget>", h);

    /// <summary>A Kenshi window with a minimize button beside its close button (Kenshi_WindowCXM); use with PzaWindow { Minimizable = true }.</summary>
    public static string WindowMin(string name, string caption, double x, double y, double w, double h, params string[] children) =>
        PagesBelowStrip($"<Widget type=\"Window\" skin=\"Kenshi_WindowCXM\" {Pos(x, y, w, h)} layer=\"Window\" name=\"{Esc(name)}\">{Prop("Caption", caption)}{string.Concat(children)}</Widget>", h);

    /// <summary>Kenshi_TabControl's tab strip and Kenshi_WindowCX's title bar, pixels at 1080 lines (they scale with the screen height in game:
    /// the binding's TemplateScale). Measured on a MyGuiScreenHost with Kenshi's skins, 2026-11-17.</summary>
    public const double TabStripPx = 35, TitleBarPx = 30, PageGapPx = 5;

    /// <summary>Meitou lays a TabItem over its whole TabControl, strip included, so a page's first row sat under the tabs (0.3.0: found by
    /// PzaFitCheck). Each TabItem is moved below the strip: its top is the strip's height as a fraction of the TabControl's height on screen
    /// (the window's height fraction x 1080 lines, less the title bar, times each parent's height fraction). The pages' own positions stay
    /// fractions of the (now smaller) page.</summary>
    static string PagesBelowStrip(string xml, double windowH)
    {
        if (!xml.Contains("\"TabControl\"", StringComparison.Ordinal)) return xml;
        var root = System.Xml.Linq.XElement.Parse(xml);
        void Walk(System.Xml.Linq.XElement el, double px)
        {
            foreach (var child in el.Elements("Widget"))
            {
                double h = px * PosH(child);
                if ((string?)child.Attribute("type") == "TabControl" && h > 0)
                {
                    double top = Math.Min(0.5, (TabStripPx + PageGapPx) / h);
                    foreach (var page in child.Elements("Widget").Where(p => (string?)p.Attribute("type") == "TabItem"))
                        page.SetAttributeValue("position_real", $"0 {F(top)} 1 {F(1 - top)}");
                    foreach (var page in child.Elements("Widget")) Walk(page, h * (1 - top));
                }
                else Walk(child, h);
            }
        }
        Walk(root, windowH * 1080 - TitleBarPx);
        return root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
    }

    static double PosH(System.Xml.Linq.XElement w)
    {
        var parts = ((string?)w.Attribute("position_real") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 4 && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 1;
    }

    /// <summary>A text line or block (Kenshi_TextboxStandardText). wrap = word wrap (top-aligned).</summary>
    public static string Text(string name, double x, double y, double w, double h, bool wrap = false, string? align = null)
    {
        var sb = new StringBuilder($"<Widget type=\"TextBox\" skin=\"Kenshi_TextboxStandardText\" {Pos(x, y, w, h)} name=\"{Esc(name)}\">");
        if (wrap) sb.Append(Prop("WordWrap", "true"));
        sb.Append(Prop("TextAlign", align ?? (wrap ? "Left Top" : "Left VCenter")));
        return sb.Append("</Widget>").ToString();
    }

    /// <summary>A Kenshi button (Kenshi_Button1). isDefault = Enter presses it.</summary>
    public static string Button(string name, double x, double y, double w, double h, bool isDefault = false) =>
        $"<Widget type=\"Button\" skin=\"Kenshi_Button1\" {Pos(x, y, w, h)} name=\"{Esc(name)}\">{(isDefault ? Prop("DefaultAction", "true") : "")}</Widget>";

    /// <summary>A tick box (Kenshi_TickBoxSkin) with its label to the right (the label is named "lb" + name).</summary>
    public static string Tick(string name, double x, double y, double size, double labelWidth) =>
        $"<Widget type=\"Button\" skin=\"Kenshi_TickBoxSkin\" {Pos(x, y, size * 0.9, size)} name=\"{Esc(name)}\"/>" +
        Text("lb" + name, x + size, y, labelWidth, size);

    /// <summary>A horizontal slider (Kenshi_SliderHBig): positions 0 .. range-1, starting at pos. Its event is "scroll" with Value = position.</summary>
    public static string Slider(string name, double x, double y, double w, double h, int range, int pos) =>
        $"<Widget type=\"ScrollBar\" skin=\"Kenshi_SliderHBig\" {Pos(x, y, w, h)} name=\"{Esc(name)}\">{Prop("Range", range.ToString(CultureInfo.InvariantCulture))}{Prop("RangePosition", pos.ToString(CultureInfo.InvariantCulture))}</Widget>";

    /// <summary>A text box the player types in (Kenshi_EditBox, or Kenshi_WordWrap when multiLine). Its event is "text" with the whole text.</summary>
    public static string Edit(string name, double x, double y, double w, double h, int maxLength = 100, bool multiLine = false) => multiLine
        ? $"<Widget type=\"EditBox\" skin=\"Kenshi_WordWrap\" {Pos(x, y, w, h)} name=\"{Esc(name)}\">{Prop("Static", "false")}{Prop("MaxTextLength", maxLength.ToString(CultureInfo.InvariantCulture))}{Prop("MultiLine", "true")}{Prop("WordWrap", "true")}{Prop("TextAlign", "Left Top")}</Widget>"
        : $"<Widget type=\"EditBox\" skin=\"Kenshi_EditBox\" {Pos(x, y, w, h)} name=\"{Esc(name)}\">{Prop("MaxTextLength", maxLength.ToString(CultureInfo.InvariantCulture))}</Widget>";

    /// <summary>A list (Kenshi_ListBox): lines from the binding (PzaWindow.Lines); events "select" / "accept" (double click), Value = line.</summary>
    public static string List(string name, double x, double y, double w, double h) =>
        $"<Widget type=\"ListBox\" skin=\"Kenshi_ListBox\" {Pos(x, y, w, h)} name=\"{Esc(name)}\"/>";

    /// <summary>A tab control (Kenshi_TabControl) with real pages; captions through PzaWindow.TabText(b, name, i, text).</summary>
    public static string Tabs(string name, double x, double y, double w, double h, params (string Page, string[] Children)[] pages)
    {
        var sb = new StringBuilder($"<Widget type=\"TabControl\" skin=\"Kenshi_TabControl\" {Pos(x, y, w, h)} align=\"Stretch\" name=\"{Esc(name)}\">");
        foreach (var (page, children) in pages)
            sb.Append($"<Widget type=\"TabItem\" position_real=\"0 0 1 1\" name=\"{Esc(page)}\">{Prop("Caption", page)}{string.Concat(children)}</Widget>");
        return sb.Append("</Widget>").ToString();
    }

    /// <summary>A character's face (an ImageBox): PzaPortrait.Face(b, name, id) in Bind fills it. Keep it square on screen.</summary>
    public static string Portrait(string name, double x, double y, double w, double h) =>
        $"<Widget type=\"ImageBox\" skin=\"ImageBox\" {Pos(x, y, w, h)} name=\"{Esc(name)}\"/>";

    /// <summary>A help page as a tab: one wrapped text filling the page (PZA UI rules 11). Only for a short help: a longer one (or one that
    /// grows in another language) spills; use <see cref="HelpList"/>.</summary>
    public static (string, string[]) HelpPage(string page, string textName) => (page, [Text(textName, 0.03, 0.03, 0.94, 0.94, wrap: true)]);

    /// <summary>A help page as a tab that scrolls (0.3.0): a list filling the page; fill it with PzaWindow.Paragraphs(b, listName, text), which
    /// wraps the text at the list's real width in the real font of the player's language. One page, however long: no "More" pages.</summary>
    public static (string, string[]) HelpList(string page, string listName) => (page, [List(listName, 0.02, 0.02, 0.96, 0.96)]);

    /// <summary>A scrolling text anywhere (a list): fill it with PzaWindow.Paragraphs.</summary>
    public static string TextList(string name, double x, double y, double w, double h) => List(name, x, y, w, h);
}
