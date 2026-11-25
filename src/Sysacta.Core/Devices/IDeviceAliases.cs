namespace Sysacta.Core.Devices;

/// <summary>User-defined device aliases (spec §8). Unique per category; resolution never falls back to another device.</summary>
public interface IDeviceAliases
{
    /// <summary>Returns the device id registered for <paramref name="alias"/> in <paramref name="category"/>, or null.</summary>
    string? Resolve(string category, string alias);
}

public static class DeviceAliasRules
{
    /// <summary>Reserved words that can never be aliases.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "all", "default" };
}
