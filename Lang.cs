using System.Globalization;

namespace ConanModCheck;

/// <summary>
/// Sprachauswahl (Englisch oder Deutsch). Die deutschen Texte im Quelltext sind zugleich die
/// Schlüssel der Übersetzungstabelle <see cref="LangEn"/>; fehlt eine Übersetzung, bleibt der
/// deutsche Text stehen.
/// </summary>
static class L
{
    /// <summary>Englisch ist Standard.</summary>
    public static bool English { get; private set; } = true;

    public static string Code => English ? "en" : "de";

    public static void Set(string? code) => English = !string.Equals(code, "de", StringComparison.OrdinalIgnoreCase);

    /// <summary>Zahlenformat passend zur Sprache, unabhängig von den Systemeinstellungen.</summary>
    public static NumberFormatInfo Numbers => English ? EnNumbers : DeNumbers;

    static readonly NumberFormatInfo EnNumbers = new() { NumberGroupSeparator = ",", NumberDecimalSeparator = "." };
    static readonly NumberFormatInfo DeNumbers = new() { NumberGroupSeparator = ".", NumberDecimalSeparator = "," };

    /// <summary>Übersetzt einen Text.</summary>
    public static string T(string german) =>
        English && LangEn.Texts.TryGetValue(german, out var en) ? en : german;

    /// <summary>Übersetzt einen Text mit Platzhaltern ({0}, {1:N0} …) und setzt die Werte ein.</summary>
    public static string F(string german, params object?[] args) => string.Format(Numbers, T(german), args);

    /// <summary>Ganzzahl mit Tausendertrennung in der gewählten Sprache.</summary>
    public static string N(long value) => value.ToString("N0", Numbers);
}
