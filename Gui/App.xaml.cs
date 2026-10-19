using System.Windows;

namespace ConanModCheck.Gui;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        ApplyLanguage(GuiSettings.Load().Language);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(Describe(args.Exception), L.T("ConanModCheck – Fehler"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
            // Ohne Hauptfenster gäbe es nichts mehr zu bedienen, dann lieber beenden
            if (MainWindow is not { IsLoaded: true }) Shutdown(1);
        };
        base.OnStartup(e);
    }

    /// <summary>Sprache setzen (Standard: Englisch), samt Zahlen- und Datumsformat der Oberfläche.</summary>
    public static void ApplyLanguage(string? code)
    {
        L.Set(code);
        var culture = System.Globalization.CultureInfo.GetCultureInfo(L.English ? "en-US" : "de-DE");
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
    }

    /// <summary>Die eigentliche Ursache steckt oft in einer inneren Ausnahme (z. B. beim Laden der Oberfläche).</summary>
    static string Describe(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner == ex ? ex.Message : $"{inner.Message}\n\n({inner.GetType().Name} in {inner.TargetSite?.DeclaringType?.Name}.{inner.TargetSite?.Name})";
    }
}
