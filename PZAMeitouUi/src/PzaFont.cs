// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// Measures text the way Meitou draws a MyGUI font resource (Kenshi_StandardFont_Medium ...), from the font file itself: no GPU, no FreeType, so
// the same numbers at run time (fitting help text into a list) and in every mod's offline check (PzaFitCheck). The font is the resource's
// Source in the game's language (locale\<xx_XX>\gui\fonts\kenshi_fonts.xml redefines the names: Exo2 for en / ru, ume-pgs5 for ja,
// TTZhongYuanJ for zh). Both of Meitou's looks are measured and the wider / taller one is used, so a text that fits here fits in either:
//   Faithful: MyGUI's size (KenshiFontSize at floor(Size x scale), 13.9 em px at 1080 lines), whole-pixel (hinted) advances, MyGUI's line height.
//   Meitou (the default): whole em pixels, at least 13.9 at 1080 lines in game (StandardTextPixels x UiScale), unhinted advances, a 1.25 em line.
// Checked against FreeType (the engine Meitou draws with) in PZA's UI Probe's offline check: equal to the pixel in en / ru / zh / ja.
// Kerning is left out (MyGUI has none; Meitou's own look kerns a little tighter). Lines wrap at spaces and between CJK characters, as Meitou's.
using System.Buffers.Binary;
using System.Xml.Linq;
using Meitou.Data.Ui;

namespace PZA.MeitouUi;

public sealed class PzaFont
{
    /// <summary>Meitou's in-game minimum text size, em pixels at 1080 lines: PlayerInterface.StandardTextPixels = 20 pt x 50 dpi / 72 = 13.9
    /// in MeitouClient 0.20.1's code (its docs/formats/mygui.md speaks of 16; the code, read 2026-11-17, passes 13.9 x UiScale to the painter).</summary>
    public const float MeitouMinimumPixels = 20 * 50 / 72f;

    readonly PzaFontFile _file;
    readonly MyGuiTrueTypeFont _spec;
    readonly float _emFaithful, _emMeitou;
    /// <summary>1.015 for hinted fonts (a few glyphs move by a pixel: Exo2 Cyrillic up to 1%), 1.04 for a font without working hinting: the auto-hinter moves advances by up to 3%
    /// (ume-pgs5 against FreeType, PZA's UI Probe check 2026-11-17).</summary>
    readonly float _hintSlack;

    /// <summary>The font file's name (for messages).</summary>
    public string Source => _spec.Source;
    /// <summary>The line height in pixels (the taller of the two looks).</summary>
    public float LineHeight { get; }
    /// <summary>The em pixels of the Faithful look (MyGUI's size) and of Meitou's own look (for the FreeType comparison in the probe's check).</summary>
    public float EmFaithful => _emFaithful;
    public float EmMeitou => _emMeitou;
    /// <summary>The resource's resolution (dpi) and points before the viewport scale.</summary>
    public float Resolution => _spec.Resolution > 0 ? _spec.Resolution : 50;
    /// <summary>The font file's path.</summary>
    public string FilePath { get; private set; } = "";
    /// <summary>The em size in pixels of the larger look (for messages).</summary>
    public float EmPixels => Math.Max(_emFaithful, _emMeitou);

    PzaFont(PzaFontFile file, MyGuiTrueTypeFont spec, float viewportHeight)
    {
        _file = file; _spec = spec;
        float scale = viewportHeight / 1080f;
        float size = spec.Size > 0 ? spec.Size : 20, res = spec.Resolution > 0 ? spec.Resolution : 50;
        float points = KenshiFontSize.Effective(file.Bytes, MathF.Floor(size * scale + 1e-3f), res);
        _emFaithful = points * res / 72;
        float meitouPoints = KenshiFontSize.Effective(file.Bytes, size, res) * scale;
        _emMeitou = MathF.Round(Math.Max(MeitouMinimumPixels * scale, meitouPoints * res / 72));
        LineHeight = Math.Max(file.DefaultHeight(_emFaithful), _emMeitou * 1.25f);
        _hintSlack = file.HasHintingProgram ? 1.015f : 1.04f;
    }

    /// <summary>The width of one line in pixels (no line breaks expected; colour tags #RRGGBB are not drawn and not counted).</summary>
    public float Width(string line)
    {
        float faithful = 0, meitou = 0;
        line = StripColours(line);
        for (int i = 0; i < line.Length; i++)
        {
            // a pair makes one code point; a lone half (a cut through an emoji, a broken name) is measured as itself (fuzz 2026-11-18: it threw)
            int cp = char.IsHighSurrogate(line[i]) && i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? char.ConvertToUtf32(line[i], line[++i]) : line[i];
            if (cp < 0x20 && cp != '\t') continue;
            if (cp == '\t') { float tab = _spec.TabWidth > 0 ? _spec.TabWidth : 8 * Advance(' ', _emFaithful); faithful += tab; meitou += tab; continue; }
            int glyph = _file.Glyph(Allowed(cp) ? cp : _spec.SubstituteCode >= 0 ? _spec.SubstituteCode : 0);
            // hinted (TrueType bytecode at FreeType's integer ppem): the advance rounded at the rounded em; the larger of that and the
            // fractional-em rounding covers fonts whose bytecode fails and fall back to the auto-hinter (ume-pgs5)
            int adv = _file.Advance(glyph);
            faithful += Math.Max(MathF.Round(adv * MathF.Round(_emFaithful) / _file.UnitsPerEm), MathF.Round(adv * _emFaithful / _file.UnitsPerEm));
            meitou += _file.Advance(_file.Glyph(cp) != 0 ? _file.Glyph(cp) : glyph) * _emMeitou / _file.UnitsPerEm;
        }
        return MathF.Ceiling(Math.Max(faithful * _hintSlack, meitou));
    }

    float Advance(int cp, float em) => MathF.Round(_file.Advance(_file.Glyph(cp)) * em / _file.UnitsPerEm);

    bool Allowed(int cp) => cp == ' ' || _spec.Codes.Ranges.Any(r => cp >= r.First && cp <= r.Last);

    /// <summary>Whether the font draws <paramref name="cp"/> (a code point outside the resource's Codes, or missing from the file, shows as a
    /// box in Kenshi's look).</summary>
    public bool Draws(int cp) => cp < 0x20 || Allowed(cp) && _file.Glyph(cp) != 0;

    /// <summary>The widest line of a text with line breaks.</summary>
    public float WidestLine(string text) => text.Replace("\\n", "\n").Split('\n').Max(l => Width(l.TrimEnd('\r')));

    /// <summary>Word-wraps a text (with its own line breaks kept) to <paramref name="width"/> pixels: breaks at spaces and between CJK characters;
    /// a word wider than the line is broken by characters. An empty paragraph stays an empty line.</summary>
    public List<string> Wrap(string text, float width)
    {
        if (float.IsNaN(width)) width = 0;   // a layout not measured yet: one character a line, never one endless line (fuzz 2026-11-18)
        var lines = new List<string>();
        foreach (var para in text.Replace("\\n", "\n").Replace("\r", "").Split('\n'))
        {
            if (para.Trim().Length == 0) { lines.Add(""); continue; }
            string line = "";
            foreach (var token in Tokens(para))
            {
                string next = line.Length == 0 ? token.TrimStart() : line + token;
                if (line.Length > 0 && Width(next.TrimEnd()) > width)
                {
                    lines.Add(line.TrimEnd());
                    line = token.TrimStart();
                }
                else line = next;
                while (line.Length > 1 && Width(line.TrimEnd()) > width)   // one word wider than the line
                {
                    int n = line.Length - 1;
                    while (n > 1 && Width(line[..n]) > width) n--;
                    lines.Add(line[..n]);
                    line = line[n..];
                }
            }
            if (line.Trim().Length > 0) lines.Add(line.TrimEnd());
        }
        return lines;
    }

    /// <summary>The height in pixels of a text wrapped to <paramref name="width"/>.</summary>
    public float WrappedHeight(string text, float width) => Wrap(text, width).Count * LineHeight;

    /// <summary>The longest start of <paramref name="text"/> (one line) that fits <paramref name="width"/> with "..." after it.</summary>
    public string Ellipsize(string text, float width)
    {
        if (float.IsNaN(width)) width = 0;
        if (Width(text) <= width) return text;
        int n = text.Length;
        while (n > 0 && Width(text[..n].TrimEnd() + "...") > width) n--;
        return text[..n].TrimEnd() + "...";
    }

    /// <summary>Splits a paragraph into wrap tokens: a word with the spaces before it, or one CJK character.</summary>
    static IEnumerable<string> Tokens(string para)
    {
        int i = 0;
        while (i < para.Length)
        {
            int start = i;
            while (i < para.Length && para[i] == ' ') i++;
            if (i < para.Length && IsCjk(para[i])) { i++; while (i < para.Length && IsClosingPunct(para[i])) i++; yield return para[start..i]; continue; }
            while (i < para.Length && para[i] != ' ' && !IsCjk(para[i])) i++;
            yield return para[start..i];
        }
    }

    static bool IsCjk(char c) => c is >= '⺀' and <= '鿿' or >= '가' and <= '힯' or >= '豈' and <= '﫿' or >= '＀' and <= '￯';
    static bool IsClosingPunct(char c) => c is '、' or '。' or '，' or '．' or '：' or '；' or '！' or '？' or '）' or '」' or '』' or '】' or '》' or '・';

    /// <summary>Drops MyGUI's inline colour codes (#RRGGBB; "##" is a literal '#').</summary>
    public static string StripColours(string s)
    {
        if (s.IndexOf('#') < 0) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '#' && i + 1 < s.Length && s[i + 1] == '#') { sb.Append('#'); i++; continue; }
            if (s[i] == '#' && i + 6 < s.Length && s.AsSpan(i + 1, 6).ToString().All(Uri.IsHexDigit)) { i += 6; continue; }
            sb.Append(s[i]);
        }
        return sb.ToString();
    }

    // ---- finding the font ----

    static readonly Dictionary<string, PzaFontFile?> s_files = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<(string, string, string, int), PzaFont?> s_fonts = [];

    /// <summary>"ru" -> "ru_RU" and so on (Kenshi's locale folders).</summary>
    public static string? LocaleFolder(string lang) => lang switch { "ru" => "ru_RU", "zh" => "zh_CN", "ja" => "ja_JP", _ => null };

    /// <summary>The font resource <paramref name="resource"/> in language <paramref name="lang"/> from a Kenshi install, at a viewport
    /// <paramref name="viewportHeight"/> lines high; null when the resource or its file is not found.</summary>
    public static PzaFont? FromInstall(string kenshiDir, string lang, string resource, int viewportHeight)
    {
        var key = (kenshiDir, lang, resource, viewportHeight);
        if (s_fonts.TryGetValue(key, out var known)) return known;
        var dirs = new List<string>();
        if (LocaleFolder(lang) is { } loc) dirs.Add(Path.Combine(kenshiDir, "locale", loc, "gui", "fonts"));
        dirs.Add(Path.Combine(kenshiDir, "data", "gui", "fonts"));
        return s_fonts[key] = Make(dirs, resource, viewportHeight);
    }

    /// <summary>The font resource as the running game loaded it: the font XML files among <paramref name="loadedFiles"/> (MyGuiResources.LoadedFiles;
    /// a locale's file after the game's own redefines the name), the font file beside it.</summary>
    public static PzaFont? FromLoaded(IEnumerable<string> loadedFiles, string resource, int viewportHeight)
    {
        var dirs = loadedFiles.Where(f => f.EndsWith("fonts.xml", StringComparison.OrdinalIgnoreCase)).Reverse()
            .Select(f => Path.GetDirectoryName(f) ?? "").Where(d => d.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var key = (string.Join("|", dirs), "", resource, viewportHeight);
        if (s_fonts.TryGetValue(key, out var known)) return known;
        return s_fonts[key] = Make(dirs, resource, viewportHeight);
    }

    static PzaFont? Make(List<string> dirs, string resource, int viewportHeight)
    {
        foreach (var dir in dirs)
        {
            string xml = Path.Combine(dir, "kenshi_fonts.xml");
            if (!File.Exists(xml)) continue;
            XElement? el;
            try { el = XDocument.Load(xml).Descendants("Resource").FirstOrDefault(r => (string?)r.Attribute("name") == resource); }
            catch (Exception) { continue; }
            if (el is null || MyGuiTrueTypeFont.Read(el) is not { } spec) continue;
            foreach (var d in dirs.Prepend(dir))
            {
                string path = Path.Combine(d, spec.Source);
                if (!File.Exists(path)) continue;
                if (!s_files.TryGetValue(path, out var file)) s_files[path] = file = PzaFontFile.Read(path);
                return file is null ? null : new PzaFont(file, spec, viewportHeight) { FilePath = path };
            }
            return null;
        }
        return null;
    }
}

/// <summary>The parts of a TrueType / OpenType file the measuring needs: units per em, hhea / OS/2 heights, cmap (formats 4 and 12), hmtx.</summary>
sealed class PzaFontFile
{
    public byte[] Bytes { get; }
    public int UnitsPerEm { get; }
    /// <summary>Whether the font has TrueType hinting programs that FreeType runs (fpgm and prep); ume-pgs5's fail, so it is listed as without.</summary>
    public bool HasHintingProgram { get; }
    readonly int _ascender, _descender, _winAscent, _winDescent, _metrics;
    readonly int _hmtx;
    readonly Dictionary<int, int> _cmap = [];

    PzaFontFile(byte[] b)
    {
        Bytes = b;
        var head = Table(b, "head") ?? throw new InvalidDataException("no head");
        var hhea = Table(b, "hhea") ?? throw new InvalidDataException("no hhea");
        _hmtx = (Table(b, "hmtx") ?? throw new InvalidDataException("no hmtx")).Offset;
        UnitsPerEm = U16(b, head.Offset + 18);
        // the TrueType range is 16..16384; 0 divided every measure by zero (fuzz 2026-11-18, a broken file)
        if (UnitsPerEm < 16 || UnitsPerEm > 16384) throw new InvalidDataException("unitsPerEm out of range");
        _ascender = I16(b, hhea.Offset + 4); _descender = I16(b, hhea.Offset + 6);
        _metrics = U16(b, hhea.Offset + 34);
        if (Table(b, "OS/2") is { Length: >= 78 } os2) { _winAscent = U16(b, os2.Offset + 74); _winDescent = U16(b, os2.Offset + 76); }
        ReadCmap(b, Table(b, "cmap") ?? throw new InvalidDataException("no cmap"));
        HasHintingProgram = Table(b, "fpgm") is not null && Table(b, "prep") is not null && !BadBytecode;
    }

    /// <summary>Set while reading a font FreeType 2.13 cannot hint (Invalid_Opcode: ume-pgs5.ttf, Meitou docs/formats/mygui.md).</summary>
    [ThreadStatic] static bool BadBytecode;

    public static PzaFontFile? Read(string path)
    {
        try { return Parse(File.ReadAllBytes(path), Path.GetFileName(path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The font in <paramref name="bytes"/>; null when it is not a font this reader can measure with (the fuzz kit feeds it broken files).</summary>
    internal static PzaFontFile? Parse(byte[] bytes, string fileName = "")
    {
        BadBytecode = fileName.Equals("ume-pgs5.ttf", StringComparison.OrdinalIgnoreCase);
        try { return new PzaFontFile(bytes); }
        catch (Exception e) when (e is InvalidDataException or IndexOutOfRangeException or ArgumentException or OverflowException) { return null; }
    }

    public int Glyph(int cp) => _cmap.TryGetValue(cp, out int g) ? g : 0;

    public int Advance(int glyph)
    {
        int i = Math.Min(glyph, _metrics - 1);
        long at = _hmtx + 4L * i;   // a short or misplaced hmtx: no advance (fuzz 2026-11-18: it threw while measuring)
        return _metrics > 0 && i >= 0 && at >= 0 && at + 2 <= Bytes.Length ? U16(Bytes, (int)at) : 0;
    }

    /// <summary>MyGUI's line height at <paramref name="em"/> pixels (KenshiFontSize's default height).</summary>
    public float DefaultHeight(float em)
    {
        int ascent = (int)MathF.Floor(_ascender * em / UnitsPerEm + 0.5f), descent = (int)MathF.Floor(-_descender * em / UnitsPerEm + 0.5f);
        int ppem = (int)MathF.Round(em);
        ascent = Math.Max(ascent, _winAscent * ppem / UnitsPerEm);
        descent = Math.Max(descent, _winDescent * ppem / UnitsPerEm);
        return ascent + descent;
    }

    void ReadCmap(byte[] b, (int Offset, int Length) cmap)
    {
        int n = U16(b, cmap.Offset + 2);
        int best = -1, bestRank = -1;
        for (int i = 0; i < n; i++)
        {
            int rec = cmap.Offset + 4 + 8 * i;
            int platform = U16(b, rec), encoding = U16(b, rec + 2);
            int sub = cmap.Offset + (int)U32(b, rec + 4);
            int format = U16(b, sub);
            int rank = format == 12 && (platform == 3 && encoding == 10 || platform == 0) ? 3
                : format == 4 && (platform == 3 && encoding == 1 || platform == 0) ? 2 : -1;
            if (rank > bestRank) { bestRank = rank; best = sub; }
        }
        if (best < 0) throw new InvalidDataException("no unicode cmap");
        if (U16(b, best) == 4)
        {
            int segs = U16(b, best + 6) / 2;
            int ends = best + 14, starts = ends + 2 * segs + 2, deltas = starts + 2 * segs, ranges = deltas + 2 * segs;
            for (int s = 0; s < segs; s++)
            {
                int end = U16(b, ends + 2 * s), start = U16(b, starts + 2 * s), delta = I16(b, deltas + 2 * s), ro = U16(b, ranges + 2 * s);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int g;
                    if (ro == 0) g = (c + delta) & 0xFFFF;
                    else
                    {
                        int at = ranges + 2 * s + ro + 2 * (c - start);
                        g = at + 1 < b.Length ? U16(b, at) : 0;
                        if (g != 0) g = (g + delta) & 0xFFFF;
                    }
                    if (g != 0) _cmap[c] = g;
                }
            }
        }
        else
        {
            // capped: a broken group count or range could otherwise map millions of code points per group (seconds, hundreds of MB)
            long groups = Math.Min(U32(b, best + 12), (b.Length - (long)best) / 12);
            for (long gi = 0; gi < groups && _cmap.Count <= 0x110000; gi++)
            {
                int at = best + 16 + (int)(12 * gi);
                long start = U32(b, at), end = U32(b, at + 4), glyph = U32(b, at + 8);
                for (long c = start; c <= end && c <= 0x10FFFF && _cmap.Count <= 0x110000; c++) _cmap[(int)c] = (int)(glyph + c - start);
            }
        }
    }

    static (int Offset, int Length)? Table(byte[] b, string tag)
    {
        int n = U16(b, 4);
        for (int i = 0; i < n; i++)
        {
            int rec = 12 + 16 * i;
            if (b[rec] == tag[0] && b[rec + 1] == tag[1] && b[rec + 2] == tag[2] && b[rec + 3] == tag[3])
                return ((int)U32(b, rec + 8), (int)U32(b, rec + 12));
        }
        return null;
    }

    static int U16(byte[] b, int at) => BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    static int I16(byte[] b, int at) => BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(at));
    static long U32(byte[] b, int at) => BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
}
