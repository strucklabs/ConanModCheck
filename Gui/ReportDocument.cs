using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace ConanModCheck.Gui;

/// <summary>
/// Macht aus dem Berichtstext ein formatiertes Dokument: Überschriften fett, Warnungen („!“)
/// rot, Hinweise („Hinweis:“, „»“) gelb hinterlegt. Der Text selbst bleibt unverändert, damit
/// Speichern und Kopieren weiterhin reinen Text liefern.
/// </summary>
static class ReportDocument
{
    static readonly Brush WarnText = Frozen(Color.FromRgb(0x9B, 0x1C, 0x1C));
    static readonly Brush WarnBack = Frozen(Color.FromRgb(0xFD, 0xEC, 0xEC));
    static readonly Brush HintText = Frozen(Color.FromRgb(0x6B, 0x50, 0x00));
    static readonly Brush HintBack = Frozen(Color.FromRgb(0xFF, 0xF4, 0xCC));

    public static FlowDocument Build(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12.5,
            PagePadding = new Thickness(8),
            // Breiter als jede Zeile, damit Tabellen nicht umbrechen
            PageWidth = 2000,
        };
        var lines = text.Replace("\r", "").Split('\n');
        int hintIndent = -1; // Einrückung eines laufenden Hinweises, -1 = keiner

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();
            int indent = line.Length - trimmed.Length;

            if (IsRule(line, '=')) continue;
            if (i + 1 < lines.Length && IsRule(lines[i + 1], '=') && i > 0 && IsRule(lines[i - 1], '='))
            {
                hintIndent = -1;
                doc.Blocks.Add(Para(line, bold: true, size: 15, top: doc.Blocks.Count > 0 ? 14 : 0));
                continue;
            }
            if (IsRule(line, '-')) continue;
            if (i + 1 < lines.Length && IsRule(lines[i + 1], '-') && line.Trim().Length > 0)
            {
                hintIndent = -1;
                doc.Blocks.Add(Para(line, bold: true, top: 6));
                continue;
            }

            if (trimmed.Length == 0) { hintIndent = -1; doc.Blocks.Add(Para(" ")); continue; }

            if (trimmed.StartsWith('!') || trimmed.StartsWith("Vorsicht:"))
            {
                hintIndent = -1;
                doc.Blocks.Add(Para(line, fg: WarnText, bg: WarnBack, bold: trimmed.StartsWith("Vorsicht:")));
                continue;
            }

            // Ein Hinweis läuft über Folgezeilen mit gleicher Einrückung weiter, bis zur Leerzeile
            if (trimmed.StartsWith("Hinweis:") || trimmed.StartsWith('»')) hintIndent = trimmed.StartsWith('»') ? -1 : indent;
            else if (hintIndent >= 0 && indent != hintIndent) hintIndent = -1;

            bool hint = hintIndent >= 0 || trimmed.StartsWith('»');
            doc.Blocks.Add(hint ? Para(line, fg: HintText, bg: HintBack) : Para(line));
        }
        return doc;
    }

    static bool IsRule(string line, char c) => line.Length >= 3 && line.Trim().Length >= 3 && line.Trim().All(ch => ch == c);

    static Paragraph Para(string text, Brush? fg = null, Brush? bg = null, bool bold = false, double? size = null, double top = 0)
    {
        var p = new Paragraph(new Run(text)) { Margin = new Thickness(0, top, 0, 0) };
        if (fg != null) p.Foreground = fg;
        if (bg != null) p.Background = bg;
        if (bold) p.FontWeight = FontWeights.SemiBold;
        if (size != null) { p.FontSize = size.Value; p.FontFamily = new FontFamily("Segoe UI"); }
        return p;
    }

    static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}
