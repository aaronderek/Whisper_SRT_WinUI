using Microsoft.UI.Xaml;
using WhisperSubtitler.Services;

namespace WhisperSubtitler;

public partial class App : Application
{
    public static Window? MainWindow { get; private set; }

    public App()
    {
        LocalizationService.ApplyLanguage(SettingsService.Load().UiLanguage);
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }

    public static void ChangeLanguage(string language)
    {
        var settings = SettingsService.Load();
        if (settings.UiLanguage == language) return;

        settings.UiLanguage = language;
        SettingsService.Save(settings);
        LocalizationService.ApplyLanguage(language);

        Window? previous = MainWindow;
        MainWindow = new MainWindow();
        MainWindow.Activate();
        previous?.Close();
    }
}
