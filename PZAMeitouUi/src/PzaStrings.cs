// PZA's Meitou UI. Author: PZA. Licence: GPLv3 or later.
// A mod's user-visible text in the player's language (English + Russian, Simplified Chinese, Japanese at every 1.0: localization plan
// 2026-11-01). English is compiled in (the table the mod passes); the other languages come from UTF-8 files in the mod folder, in the same
// format as the RE_Kenshi plugins' (src/StringTable.h): "key = text", '#' / ';' comments, escapes \n \\ \= \#; a line whose {1}..{9} set differs
// from the English line is refused (English shown); keys the mod does not know are skipped (a file can be shared with an RE_Kenshi plugin).
// A translation is never a format string: {n} is replaced here. Log lines stay English. Language: Meitou's own (Loc.Language.Code), or the
// mod's Language setting ("auto" / en / ru / zh / ja).
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PZA.MeitouUi;

public sealed class PzaStrings((string Key, string En)[] table)
{
    readonly Dictionary<string, string> _en = table.ToDictionary(t => t.Key, t => t.En, StringComparer.Ordinal);
    readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);

    /// <summary>The language in use: "en", "ru", "zh" or "ja".</summary>
    public string Lang { get; private set; } = "en";

    /// <summary>"ru_RU" / "ru" / "RU" -> "ru"; anything else (de, fr, ...) -> null.</summary>
    public static string? CodeFor(string? v)
    {
        if (string.IsNullOrEmpty(v) || v.Length < 2) return null;
        string c = v[..2].ToLowerInvariant();
        return c is "en" or "ru" or "zh" or "ja" ? c : null;
    }

    /// <summary>Picks the language and loads its files (each "{code}" in <paramref name="files"/> replaced, relative to the mod folder: e.g.
    /// "lang/{code}.txt"). Returns the log lines (English).</summary>
    public List<string> Init(string modFolder, string gameLanguage, string setting, params string[] files)
    {
        var log = new List<string>();
        _text.Clear();
        string code = CodeFor(gameLanguage) ?? "en";
        if (!string.Equals(setting, "auto", StringComparison.OrdinalIgnoreCase)) code = CodeFor(setting) ?? code;
        Lang = code;
        log.Add($"language: the game runs in '{gameLanguage}', setting '{setting}' -> {code}");
        if (code == "en") return log;
        foreach (var pattern in files)
        {
            string file = Path.Combine(modFolder, pattern.Replace("{code}", code));
            if (!File.Exists(file)) { log.Add($"language: {pattern.Replace("{code}", code)} not found - English for its keys"); continue; }
            var (ok, refused) = Load(File.ReadAllBytes(file), _text);
            log.Add($"language: {Path.GetFileName(file)}: {ok} texts used, {refused.Count} line(s) refused" + (refused.Count > 0 ? ": " + string.Join(", ", refused.Take(10)) : ""));
        }
        var missing = _en.Keys.Where(k => !_text.ContainsKey(k)).ToList();
        if (missing.Count > 0) log.Add("language: shown in English: " + string.Join(", ", missing.Take(40)) + (missing.Count > 40 ? ", ..." : ""));
        return log;
    }

    static readonly Regex Placeholder = new(@"\{([1-9])\}", RegexOptions.CultureInvariant);
    static string PlaceholderSet(string s) => string.Concat(Placeholder.Matches(s).Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    /// <summary>Parses one language file into <paramref name="into"/> (only keys this table knows). Returns the count used and the refused lines.</summary>
    public (int Used, List<string> Refused) Load(byte[] data, Dictionary<string, string> into)
    {
        var refused = new List<string>();
        int used = 0;
        string text;
        try { text = new UTF8Encoding(false, true).GetString(data); }
        catch (DecoderFallbackException) { refused.Add("file: not valid UTF-8"); return (0, refused); }
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        string[] lines = text.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n].TrimEnd('\r').Trim(' ', '\t');
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) { refused.Add($"line {n + 1} no key"); continue; }
            string key = line[..eq].TrimEnd(' ', '\t');
            if (!_en.TryGetValue(key, out var english)) continue;
            string raw = line[(eq + 1)..].TrimStart(' ', '\t');
            var sb = new StringBuilder();
            bool bad = raw.Length == 0;
            for (int i = 0; i < raw.Length && !bad; i++)
            {
                char c = raw[i];
                if (c == '\t' || c < 0x20) { bad = true; break; }
                if (c != '\\') { sb.Append(c); continue; }
                if (i + 1 >= raw.Length) { bad = true; break; }
                char d = raw[++i];
                if (d == 'n') sb.Append('\n'); else if (d is '\\' or '=' or '#') sb.Append(d); else bad = true;
            }
            if (bad) { refused.Add($"line {n + 1} {key} (bad text or escape)"); continue; }
            string val = sb.ToString();
            if (PlaceholderSet(val) != PlaceholderSet(english)) { refused.Add($"line {n + 1} {key} (placeholders differ from English)"); continue; }
            if (into.ContainsKey(key)) { refused.Add($"line {n + 1} {key} (duplicate, first kept)"); continue; }
            into[key] = val; used++;
        }
        return (used, refused);
    }

    /// <summary>The text for a key (English when missing; the key itself when unknown, so a gap shows).</summary>
    public string T(string key) => _text.TryGetValue(key, out var s) ? s : _en.TryGetValue(key, out var e) ? e : key;

    /// <summary>The key whose text (in the language in use) <paramref name="shown"/> is, {n} matching anything; null when none (offline checks
    /// name the string this way).</summary>
    public string? KeyOf(string shown)
    {
        foreach (var k in _en.Keys) if (T(k) == shown) return k;
        foreach (var k in _en.Keys)
        {
            string t = T(k);
            if (!Placeholder.IsMatch(t)) continue;
            string pattern = "^" + Placeholder.Replace(Regex.Escape(t).Replace("\\{", "{"), ".*") + "$";
            if (Regex.IsMatch(shown, pattern, RegexOptions.Singleline)) return k;
        }
        foreach (var k in _en.Keys) if (T(k).Length > 12 && shown.Contains(T(k), StringComparison.Ordinal)) return k + " (part)";
        return null;
    }

    /// <summary>T(key) with {1}..{9} replaced (never a format string).</summary>
    public string TF(string key, params string[] args)
    {
        string s = T(key);
        for (int i = 0; i < args.Length && i < 9; i++) s = s.Replace("{" + (i + 1).ToString(CultureInfo.InvariantCulture) + "}", args[i]);
        return s;
    }
}
