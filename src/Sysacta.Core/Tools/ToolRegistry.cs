using System.Text.RegularExpressions;

namespace Sysacta.Core.Tools;

/// <summary>The fixed set of tools of this process. Validates naming rules at construction (fail fast).</summary>
public sealed partial class ToolRegistry
{
    private readonly Dictionary<string, SysactaTool> _tools;

    public ToolRegistry(IEnumerable<SysactaTool> tools)
    {
        _tools = new(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var d = tool.Descriptor;
            if (!NamePattern().IsMatch(d.Name) || !d.Name.StartsWith(d.Category + "_", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Tool name '{d.Name}' must be '<category>_<verb>_<object>' in lowercase and start with its category '{d.Category}'.");
            }

            if (!_tools.TryAdd(d.Name, tool))
            {
                throw new ArgumentException($"Duplicate tool name '{d.Name}'.");
            }

            if (d.EffectiveTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException($"Tool '{d.Name}' has a non-positive timeout.");
            }
        }
    }

    public IReadOnlyCollection<SysactaTool> All => _tools.Values;

    public SysactaTool? Find(string name) => _tools.GetValueOrDefault(name);

    [GeneratedRegex("^[a-z]+(_[a-z0-9]+){1,4}$")]
    private static partial Regex NamePattern();
}
