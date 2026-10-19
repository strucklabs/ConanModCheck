using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Macht aus dem Berichtstext ein formatiertes Dokument: Überschriften abgesetzt, Aufzählungen
/// mit Punkten, Warnungen („!“) rot, Hinweise („Hinweis:“, „»“) gelb hinterlegt. Fließtext
/// bricht an der Fensterbreite um; Folgezeilen rücken unter den Text ein. Nur Tabellen bleiben
/// in fester Schrift. Der Text selbst bleibt unverändert, damit Speichern und Kopieren weiterhin
/// reinen Text liefern.
/// </summary>
static class ReportDocument
{
    static readonly Brush WarnText = Frozen(Color.FromRgb(0x9B, 0x1C, 0x1C));
    static readonly Brush WarnBack = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));
    static readonly Brush HintText = Frozen(Color.FromRgb(0x6B, 0x50, 0x00));
    static readonly Brush HintBack = Frozen(Color.FromRgb(0xFF, 0xF4, 0xCC));
    static readonly Brush RuleBrush = Frozen(Color.FromRgb(0xD8, 0xD4, 0xCA));
    static readonly Brush SubText = Frozen(Color.FromRgb(0x3A, 0x4A, 0x5C));

    static readonly FontFamily Text = new("Segoe UI");
    static readonly FontFamily Mono = new("Consolas");

    const double IndentPerSpace = 7;   // Einrückung je Leerzeichen im Text, in Pixeln
    const double Hang = 18;            // Folgezeilen umbrochener Absätze rücken so weit ein

    // Tabellenzeile: Spalten mit mehreren Leerzeichen dazwischen
    static readonly Regex TableGap = new(@"\S {2,}\S");
    static readonly Regex WideGap = new(@"\S {3,}\S");

    public static FlowDocument Build(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = Text,
            FontSize = 13,
            PagePadding = new Thickness(14, 10, 14, 14),
            TextAlignment = TextAlignment.Left,
        };
        var lines = text.Replace("\r", "").Split('\n');
        int hintIndent = -1;       // Einrückung eines laufenden Hinweises, -1 = keiner
        bool hintOpen = false;     // die letzte Hinweiszeile endet mitten im Satz
        bool gap = false;          // vorher kam eine Leerzeile: nächsten Absatz etwas absetzen

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (IsRule(line, '=')) continue;
            if (i + 1 < lines.Length && IsRule(lines[i + 1], '=') && i > 0 && IsRule(lines[i - 1], '='))
            {
                hintIndent = -1; gap = false;
                var h = Para(trimmed, top: doc.Blocks.Count > 0 ? 22 : 0);
                h.FontSize = 17;
                h.FontWeight = FontWeights.SemiBold;
                h.BorderBrush = RuleBrush;
                h.BorderThickness = new Thickness(0, 0, 0, 1);
                h.Padding = new Thickness(0, 0, 0, 3);
                h.Margin = new Thickness(0, h.Margin.Top, 0, 6);
                doc.Blocks.Add(h);
                continue;
            }
            if (IsRule(line, '-')) continue;
            if (i + 1 < lines.Length && IsRule(lines[i + 1], '-') && line.Trim().Length > 0)
            {
                hintIndent = -1; gap = false;
                var h = Para(trimmed, left: indent * IndentPerSpace, top: 12);
                h.FontSize = 14;
                h.FontWeight = FontWeights.SemiBold;
                h.Foreground = SubText;
                h.Margin = new Thickness(h.Margin.Left, 12, 0, 3);
                doc.Blocks.Add(h);
                continue;
            }

            if (trimmed.Length == 0) { hintIndent = -1; gap = true; continue; }
            double top = gap ? 9 : 1;
            gap = false;

            // Tabellen in fester Schrift, sonst stehen die Spalten nicht untereinander
            if (TableGap.Matches(trimmed).Count >= 2 || WideGap.IsMatch(trimmed))
            {
                hintIndent = -1;
                var t = Para(line.TrimEnd(), top: top);
                t.FontFamily = Mono;
                t.FontSize = 12.5;
                doc.Blocks.Add(t);
                continue;
            }

            double left = indent * IndentPerSpace;

            if (trimmed.StartsWith('!') || trimmed.StartsWith(L.T("Vorsicht:")))
            {
                hintIndent = -1;
                bool title = trimmed.StartsWith(L.T("Vorsicht:"));
                var body = title ? trimmed : "⚠ " + trimmed.TrimStart('!').TrimStart();
                var p = Para(body, left: left, top: top, hang: !title, boxed: true);
                p.Foreground = WarnText;
                p.Background = WarnBack;
                if (title) p.FontWeight = FontWeights.SemiBold;
                doc.Blocks.Add(p);
                continue;
            }

            // Ein Hinweis läuft nur weiter, wenn seine Zeile mitten im Satz endet und die nächste
            // gleich weit eingerückt ist; die Folgezeile wird angehängt, damit der Absatz frei umbricht
            bool continues = false;
            if (trimmed.StartsWith(L.T("Hinweis:")) || trimmed.StartsWith('»')) hintIndent = trimmed.StartsWith('»') ? -1 : indent;
            else if (hintIndent >= 0 && (indent != hintIndent || !hintOpen)) hintIndent = -1;
            else if (hintIndent >= 0) continues = true;
            hintOpen = hintIndent >= 0 && !".!?:)".Contains(trimmed[^1]);
            bool hint = hintIndent >= 0 || trimmed.StartsWith('»');
            if (continues && top <= 1 && doc.Blocks.LastBlock is Paragraph last)
            {
                last.Inlines.Add(new Run(" " + trimmed));
                continue;
            }

            Paragraph para;
            if (trimmed.StartsWith("- ")) para = Para("•  " + trimmed[2..], left: left, top: top, hang: true, boxed: hint);
            else if (trimmed.StartsWith("» ")) para = Para("»  " + trimmed[2..], left: left, top: top, hang: true, boxed: hint);
            else para = Para(trimmed, left: left, top: top, hang: true, boldLabel: !hint, boxed: hint);

            if (hint)
            {
                para.Foreground = HintText;
                para.Background = HintBack;
            }
            doc.Blocks.Add(para);
        }
        return doc;
    }

    static bool IsRule(string line, char c) => line.Length >= 3 && line.Trim().Length >= 3 && line.Trim().All(ch => ch == c);

    /// <param name="hang">Folgezeilen beim Umbruch einrücken (hängender Einzug).</param>
    /// <param name="boldLabel">Eine kurze Bezeichnung vor einem Doppelpunkt fett setzen, z. B. „Erlernt:“.</param>
    /// <param name="boxed">Absatz mit Hintergrund: Der Einzug liegt dann im Innenabstand, damit der
    /// Hintergrund am Anfang der ersten Zeile beginnt und nicht erst beim Einzug der Folgezeilen.</param>
    static Paragraph Para(string text, double left = 0, double top = 0, bool hang = false, bool boldLabel = false, bool boxed = false)
    {
        var p = new Paragraph();
        if (boxed)
        {
            p.Margin = new Thickness(left, top, 0, 0);
            p.Padding = new Thickness(4 + (hang ? Hang : 0), 1, 4, 1);
        }
        else p.Margin = new Thickness(left + (hang ? Hang : 0), top, 0, 0);
        if (hang) p.TextIndent = -Hang;

        int colon = boldLabel ? text.IndexOf(": ", StringComparison.Ordinal) : -1;
        // Nur echte Bezeichnungen: kurz, und der Text danach geht weiter
        if (colon is > 0 and <= 40 && colon + 2 < text.Length && !text[..colon].Contains('(') && !text[..colon].Contains('“'))
        {
            p.Inlines.Add(new Run(text[..(colon + 1)]) { FontWeight = FontWeights.SemiBold });
            p.Inlines.Add(new Run(text[(colon + 1)..]));
        }
        else p.Inlines.Add(new Run(text));
        return p;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
