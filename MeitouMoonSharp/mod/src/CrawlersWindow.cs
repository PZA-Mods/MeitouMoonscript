// MeitouMoonscript: clickable Cat Crawlers window on PZA's Meitou UI (/crawlers or F10 > Mods). Author: PZA. Licence: GPLv3 or later.
// Runs scripts/crawlers.lua via CrawlersSession so players click FISH / ROLL BACK / TAKE / RIDE / DEAL instead of typing Lua calls.
using System.Globalization;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;
using PZA.MeitouUi;
using PZA.Moonscript.Repl;
using L = PZA.MeitouUi.PzaLayout;

namespace PZA.Moonscript;

public static class CrawlersWindow
{
    public const string Id = "pza.moonscript.crawlers";
    public const int LogRows = 4;
    public static PzaWindow Window = null!;
    public static CrawlersSession? Session;
    static string s_folder = "";

    static string[] Page()
    {
        var w = new List<string>
        {
            L.Text("lbHeader", 0.02, 0.015, 0.96, 0.055),
            "<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.02 0.08 0.56 0.25\" name=\"CardPanel\"/>",
            L.Text("lbCard", 0.04, 0.10, 0.52, 0.08, align: "Center"),
            L.Text("lbPot", 0.04, 0.185, 0.52, 0.06, align: "Center"),
            L.Text("lbOdds", 0.04, 0.25, 0.52, 0.06, align: "Center"),

            // Ladder card counts on the right
            "<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.60 0.08 0.38 0.42\" name=\"LadderPanel\"/>",
            L.Text("lbLadTitle", 0.62, 0.09, 0.34, 0.045),
        };
        for (int i = 1; i <= 7; i++)
            w.Add(L.Text("lad" + i, 0.62, 0.14 + (i - 1) * 0.048, 0.34, 0.045));

        // Primary move buttons
        w.Add(L.Button("btnFish", 0.02, 0.35, 0.27, 0.075));
        w.Add(L.Button("btnRoll", 0.31, 0.35, 0.27, 0.075));
        w.Add(L.Button("btnTake", 0.02, 0.435, 0.27, 0.065));
        w.Add(L.Button("btnRide", 0.31, 0.435, 0.27, 0.065));

        // Deal stake buttons
        w.Add(L.Button("btnDeal100", 0.02, 0.525, 0.30, 0.065));
        w.Add(L.Button("btnDeal200", 0.35, 0.525, 0.30, 0.065));
        w.Add(L.Button("btnDeal500", 0.68, 0.525, 0.30, 0.065));

        // Recent table log
        w.Add("<Widget type=\"ImageBox\" skin=\"ImageBox\" position_real=\"0.02 0.61 0.96 0.35\" name=\"LogPanel\"/>");
        for (int i = 1; i <= LogRows; i++)
            w.Add(L.Text("clog" + i, 0.04, 0.63 + (i - 1) * 0.075, 0.92, 0.07));
        return [.. w];
    }

    static string LayoutXml() => L.Doc(L.WindowMin("Root", "MOONSCRIPT CAT CRAWLERS", 0.24, 0.15, 0.52, 0.66,
        L.Tabs("Tabs", 0.02, 0.02, 0.96, 0.96, ("TabTable", Page()), L.HelpList("TabHelp", "lstHelp"))));

    public static void Setup(string folder)
    {
        s_folder = folder;
        Window = new PzaWindow(Id, LayoutXml) { Minimizable = true, Bind = Bind, Event = Event };
        PzaUi.Add(Window);
    }

    public static void Open()
    {
        Session?.Open();
        Window.Open();
    }

    static void Bind(UiBindings b)
    {
        PzaWindow.TabText(b, "Tabs", 0, "CAT CRAWLERS");
        PzaWindow.TabText(b, "Tabs", 1, "HELP");
        Window.Paragraphs(b, "lstHelp", Help);
        string panel = "file:" + Path.Combine(s_folder, "ui", "panel.png");
        PzaWindow.Props(b, "CardPanel", ("ImageTexture", panel));
        PzaWindow.Props(b, "LadderPanel", ("ImageTexture", panel));
        PzaWindow.Props(b, "LogPanel", ("ImageTexture", panel));

        if (Session is not { } s) return;
        string netStr = s.Net >= 0 ? "+" + s.Net : s.Net.ToString(CultureInfo.InvariantCulture);
        b.Text["lbHeader"] = $"Purse: {s.Purse} practice cats (net {netStr})   |   Crawl: {s.Total}/28 left";
        PzaWindow.Props(b, "lbHeader", ("TextColour", "#F0B45A"));

        string faceName = s.Face is >= 1 and <= 7 ? $"[{s.Face}] {CrawlersSession.Names[s.Face - 1]}" : "No card dealt";
        string drawnName = s.Drawn is >= 1 and <= 7 ? $"[{s.Drawn}] {CrawlersSession.Names[s.Drawn - 1]}" : "";
        b.Text["lbCard"] = s.Phase == 2 ? $"LICK!  {drawnName}" : s.Phase == 3 ? $"DRYOUT  ({drawnName})" : faceName;
        PzaWindow.Props(b, "lbCard", ("TextColour", s.Phase == 2 ? "#5FD7AF" : s.Phase == 3 ? "#FF6B6B" : "#E6E6E6"));
        b.Text["lbPot"] = s.Pot > 0 ? $"Pot: {s.Pot} cats  (Hot Chain x{s.Chain})" : "Pick a stake below to deal";
        PzaWindow.Props(b, "lbPot", ("TextColour", "#9AE6FF"));
        b.Text["lbOdds"] = s.Note ?? (s.CanCall
            ? $"Fish ({7 - s.Face}/6 -> {s.PaysFish})  |  Roll ({s.Face - 1}/6 -> {s.PaysRoll})"
            : s.CanTake ? "TAKE to pocket the pot, or RIDE for a hot chain!" : "Practice table: never touches your real cats.");
        PzaWindow.Props(b, "lbOdds", ("TextColour", s.Note is not null ? "#FF6B6B" : "#C9B78F"));

        b.Text["lbLadTitle"] = "7-CRAWLER LADDER";
        PzaWindow.Props(b, "lbLadTitle", ("TextColour", "#C8A2FF"));
        for (int i = 1; i <= 7; i++)
        {
            bool cur = (s.Phase == 1 && s.Face == i) || (s.Phase == 2 && s.Drawn == i);
            b.Text["lad" + i] = $"[{i}] {CrawlersSession.Names[i - 1],-17} {s.Left[i - 1]}/4{(cur ? " <" : "")}";
            PzaWindow.Props(b, "lad" + i, ("TextColour", cur ? "#5FD7AF" : "#E6E6E6"));
        }

        b.Text["btnFish"] = s.CanCall ? $"FISH!  (+{s.PaysFish})" : "FISH! (higher)";
        b.Text["btnRoll"] = s.CanCall ? $"ROLL BACK!  (+{s.PaysRoll})" : "ROLL BACK! (lower)";
        b.Text["btnTake"] = s.CanTake ? $"TAKE {s.Pot} CATS" : "TAKE";
        b.Text["btnRide"] = s.CanRide ? "RIDE (HOT CHAIN)" : "RIDE";
        b.Text["btnDeal100"] = "DEAL 100";
        b.Text["btnDeal200"] = "DEAL 200";
        b.Text["btnDeal500"] = "DEAL 500";

        if (s.CanCall) { PzaWindow.Enable(b, "btnFish"); PzaWindow.Enable(b, "btnRoll"); }
        if (s.CanTake) PzaWindow.Enable(b, "btnTake");
        if (s.CanRide) PzaWindow.Enable(b, "btnRide");
        if (s.CanDeal) { PzaWindow.Enable(b, "btnDeal100"); PzaWindow.Enable(b, "btnDeal200"); PzaWindow.Enable(b, "btnDeal500"); }

        for (int i = 1; i <= LogRows; i++)
        {
            int idx = s.Lines.Count - LogRows + i - 1;
            b.Text["clog" + i] = idx >= 0 ? s.Lines[idx] : " ";
            PzaWindow.Props(b, "clog" + i, ("TextColour", i == LogRows ? "#E6E6E6" : "#9AA3AD"));
        }
    }

    static void Event(MyGuiEvent e)
    {
        if (Session is not { } s) return;
        string a = e.Action.Length > 0 ? e.Action : e.Widget;
        switch (a)
        {
            case "btnFish": s.Action("fish"); return;
            case "btnRoll": s.Action("roll"); return;
            case "btnTake": s.Action("take"); return;
            case "btnRide": s.Action("ride"); return;
            case "btnDeal100": s.Action("deal", 100); return;
            case "btnDeal200": s.Action("deal", 200); return;
            case "btnDeal500": s.Action("deal", 500); return;
        }
    }

    const string Help =
        "Cat Crawlers (Swamp Bar Game) running on scripts/crawlers.lua, by PZA.\n\n" +
        "Seven crawlers from 1 Wolf Tail up to 7 Plank, four copies each (28-card crawl). Click FISH! to bet the next card is higher, or ROLL BACK! for lower. Ties are a dryout (the house cut). After a lick, click TAKE to pocket the pot or RIDE to let it ride on a hot chain up to 6,000.\n\n" +
        "Open with /crawlers in the terminal (Shift+Y) or F10 > Mods. Practice cats only: nothing in your save changes.";
}
