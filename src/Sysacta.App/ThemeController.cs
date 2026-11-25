using System.Windows;
using Microsoft.Win32;
using Sysacta.Infrastructure;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Sysacta.App;

/// <summary>Light / Dark / follow Windows.</summary>
internal static class ThemeController
{
    private static Window? _watched;

    public static void Apply(ThemePreference preference, Window? window)
    {
        if (preference == ThemePreference.System)
        {
            ApplicationThemeManager.Apply(SystemPrefersLight() ? ApplicationTheme.Light : ApplicationTheme.Dark, WindowBackdropType.Mica, true);
            if (window is not null && _watched != window)
            {
                SystemThemeWatcher.Watch(window, WindowBackdropType.Mica, true);
                _watched = window;
            }

            return;
        }

        // SystemThemeWatcher.UnWatch throws for a window that is not loaded yet (e.g. at startup with a saved
        // Light/Dark preference), and there is nothing to unwatch if we never watched it.
        if (window is not null && _watched == window && window.IsLoaded)
        {
            SystemThemeWatcher.UnWatch(window);
            _watched = null;
        }

        ApplicationThemeManager.Apply(preference == ThemePreference.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, WindowBackdropType.Mica, true);
    }

    private static bool SystemPrefersLight()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }
}
