using System.Management;
using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Display;

namespace Sysacta.Windows.Display;

/// <summary>
/// Internal panel brightness (root\wmi WmiMonitorBrightness / WmiMonitorBrightnessMethods). The WMI instance is matched
/// to the display by its PnP instance id; the first or every instance is never used instead.
/// </summary>
internal sealed class WmiPanelControl(string instanceId, IDisposable release) : IDisplayControl
{
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(5);
    private readonly List<IDisposable> _owned = [];
    private bool _disposed;

    public DisplayControlMethod Method => DisplayControlMethod.Wmi;

    public LevelRange Read()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return Guard(() =>
        {
            using var panel = Find("WmiMonitorBrightness");
            var levels = panel["Level"] as byte[];
            if (levels is null || levels.Length == 0)
            {
                throw new ToolFailureException(ErrorCodes.NotSupported, "Windows reports no brightness levels for this panel.");
            }

            // Brightness is a percentage; the panel accepts only the listed values.
            return new LevelRange(0, 100, (byte)panel["CurrentBrightness"], [.. levels.Select(l => (long)l)]);
        });
    }

    public Action PrepareWrite(long native)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var value = checked((byte)native);
        var (panel, parameters) = Guard(() =>
        {
            var methods = Find("WmiMonitorBrightnessMethods");
            _owned.Add(methods);
            var input = methods.GetMethodParameters("WmiSetBrightness");
            _owned.Add(input);
            input["Timeout"] = 0u; // apply now
            input["Brightness"] = value;
            return (methods, input);
        });

        return () =>
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _ = Guard(() =>
            {
                using var result = panel.InvokeMethod("WmiSetBrightness", parameters, new InvokeMethodOptions { Timeout = QueryTimeout });
                if (result?.Properties.Cast<PropertyData>().FirstOrDefault(p => p.Name == "ReturnValue")?.Value is { } code
                    && Convert.ToUInt32(code, System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    throw new ToolFailureException(ErrorCodes.OsApiError, "Windows could not set the panel brightness.");
                }

                return 0;
            });
        };
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _owned.ForEach(o => o.Dispose());
            release.Dispose();
        }
    }

    /// <summary><c>\\?\DISPLAY#ABC123#5&amp;1&amp;0&amp;UID1#{guid}</c> to <c>DISPLAY\ABC123\5&amp;1&amp;0&amp;UID1</c>, or null.</summary>
    internal static string? InstanceId(string devicePath)
    {
        var parts = devicePath.Split('#');
        return parts is [@"\\?\DISPLAY", var model, var instance, _] ? $@"DISPLAY\{model}\{instance}" : null;
    }

    /// <summary>WMI instance names are the PnP instance id followed by "_" and a number.</summary>
    internal static bool Matches(string instanceName, string instanceId)
    {
        var end = instanceName.LastIndexOf('_');
        return end > 0 && end < instanceName.Length - 1 && instanceName[(end + 1)..].All(char.IsAsciiDigit)
            && string.Equals(instanceName[..end], instanceId, StringComparison.OrdinalIgnoreCase);
    }

    private ManagementObject Find(string className)
    {
        // The query is a constant; the instance is selected here rather than by building WQL from device data.
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\wmi"),
            new ObjectQuery($"SELECT * FROM {className}"),
            new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = false });
        using var collection = searcher.Get();
        var matches = new List<ManagementObject>(1);
        foreach (var item in collection.Cast<ManagementObject>())
        {
            if (item["Active"] is true && item["InstanceName"] is string name && Matches(name, instanceId))
            {
                matches.Add(item);
            }
            else
            {
                item.Dispose();
            }
        }

        if (matches.Count == 1)
        {
            return matches[0];
        }

        matches.ForEach(m => m.Dispose());
        throw matches.Count == 0
            ? new ToolFailureException(ErrorCodes.NotSupported, "Windows exposes no brightness control for this panel.")
            : new ToolFailureException(ErrorCodes.AmbiguousTarget, "Several Windows brightness controls match this panel.");
    }

    private static T Guard<T>(Func<T> action)
    {
        try
        {
            return action();
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.NotSupported)
        {
            throw new ToolFailureException(ErrorCodes.NotSupported, "This computer does not support Windows brightness control.");
        }
        catch (ManagementException ex) when (ex.ErrorCode == ManagementStatus.AccessDenied)
        {
            throw new ToolFailureException(ErrorCodes.AdminRequired, "Access to the Windows brightness control was denied.");
        }
        catch (Exception ex) when (ex is ManagementException or COMException or UnauthorizedAccessException or InvalidCastException)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows brightness control returned an error.");
        }
    }
}
