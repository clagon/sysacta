using System.Runtime.InteropServices;
using System.Windows;

namespace Sysacta.App;

internal static class ClipboardHelper
{
    /// <summary>The clipboard can be locked by another process; report failure instead of crashing.</summary>
    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(50);
            }
        }

        return false;
    }
}
