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

            tool.ValidateAuditedArguments();

            if (d.EffectiveTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentException($"Tool '{d.Name}' has a non-positive timeout.");
            }
        }
    }

    /// <summary>The MCP tools (everything except <see cref="ToolDescriptor.LocalOnly"/> tools).</summary>
    public IReadOnlyCollection<SysactaTool> All => [.. _tools.Values.Where(t => !t.Descriptor.LocalOnly)];

    /// <summary>Every registered tool name, including local-only tools (for filtering the audit log).</summary>
    public IReadOnlyCollection<string> AllNames => _tools.Keys;

    /// <summary>Any registered tool, including local-only ones. The executor refuses local-only tools to MCP clients.</summary>
    public SysactaTool? Find(string name) => _tools.GetValueOrDefault(name);

    [GeneratedRegex("^[a-z]+(_[a-z0-9]+){1,4}$")]
    private static partial Regex NamePattern();
}
