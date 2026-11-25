using System.Windows;
using Microsoft.Win32;
using Sysacta.Infrastructure;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Sysacta.App;

/// <summary>Light / Dark / follow Windows.</summary>
internal static class ThemeController
{
    public static void Apply(ThemePreference preference, Window? window)
    {
        if (preference == ThemePreference.System)
        {
            ApplicationThemeManager.Apply(SystemPrefersLight() ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica, true);
            if (window is not null)
            {
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
            }

            return;
        }

        if (window is not null)
        {
            SystemThemeWatcher.UnWatch(window);
        }

        ApplicationThemeManager.Apply(preference == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
    }

    private static bool SystemPrefersLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
