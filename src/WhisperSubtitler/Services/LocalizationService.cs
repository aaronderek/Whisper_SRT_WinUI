using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;
using Windows.System.UserProfile;

namespace WhisperSubtitler.Services;

public static class LocalizationService
{
    public const string FollowSystem = "system";
    private static ResourceLoader? _loader;

    public static void ApplyLanguage(string? language)
    {
        ApplicationLanguages.PrimaryLanguageOverride = ResolveLanguage(language);
        _loader = null;
    }

    public static string ResolveLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language) && language != FollowSystem)
            return language;

        string preferred = GlobalizationPreferences.Languages.FirstOrDefault()
            ?? CultureInfo.InstalledUICulture.Name;
        return preferred.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en-US";
    }

    public static string Get(string key)
    {
        try
        {
            _loader ??= new ResourceLoader();
            string value = _loader.GetString(key);
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    public static string Format(string key, params object?[] args)
        => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
