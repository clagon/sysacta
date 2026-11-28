namespace Sysacta.Core.Tools.Network;

/// <summary>
/// Fail-closed safety rules for network_set_adapter_enabled (docs/phase-2b-progress.md, "通信経路の安全性判定").
/// <para>
/// A disable is allowed only for an adapter that cannot be carrying any traffic now: a physical wired / Wi-Fi adapter whose
/// link is down and media disconnected, with no IP address in any state, no route and no stacking relation, judged from
/// complete OS tables. This does not depend on knowing the MCP connection's path (which a loopback proxy such as Tailscale
/// Serve hides), so loopback connections, forwarded headers or the caller's address are never used as evidence.
/// </para>
/// </summary>
public static class NetworkGuard
{
    /// <summary>
    /// The IP Helper part of the rules for changing <paramref name="adapter"/> to enabled (<paramref name="enable"/>) or
    /// disabled. Null when these tables do not rule the change out.
    /// </summary>
    public static NetworkChangeBlock? Check(NetworkAdapter adapter, bool enable, NetworkAdapterSet set, NetworkSnapshot snapshot)
    {
        if (adapter.Kind is not (NetworkAdapterKind.Wired or NetworkAdapterKind.Wifi) || !adapter.Row.ConnectorPresent)
        {
            return NetworkChangeBlock.Unsupported;
        }

        if (!set.Complete || !snapshot.Addresses.IsComplete || !snapshot.Routes.IsComplete || !snapshot.Stack.IsComplete)
        {
            return NetworkChangeBlock.InformationIncomplete;
        }

        if (HasDependency(adapter.Row.Index, snapshot))
        {
            return NetworkChangeBlock.DependencyPresent;
        }

        if (enable)
        {
            return null;
        }

        if (adapter.OperState is not (NetworkOperState.Down or NetworkOperState.LowerLayerDown) || adapter.MediaState != NetworkMediaState.Disconnected)
        {
            return NetworkChangeBlock.ActiveAdapter;
        }

        // A running Windows always has loopback addresses and routes: an empty table is not trusted as "nothing uses it".
        if (snapshot.Addresses.Rows.Count == 0 || snapshot.Routes.Rows.Count == 0)
        {
            return NetworkChangeBlock.InformationIncomplete;
        }

        // Mobile Hotspot / Wi-Fi Direct run on separate virtual 802.11 interfaces over the same radio, without a stack
        // relation: while any of them may be in use, the physical Wi-Fi adapter is not disabled.
        if (adapter.Kind == NetworkAdapterKind.Wifi && snapshot.Interfaces.Rows.Any(r => r.IfType == 71 && !r.HardwareInterface && !r.FilterInterface
            && (r.OperStatus is not (2 or 6 or 7) || r.MediaConnectState != 2 || snapshot.Addresses.Rows.Any(a => a.Luid == r.Luid))))
        {
            return NetworkChangeBlock.DependencyPresent;
        }

        if (snapshot.Addresses.Rows.Any(a => a.Luid == adapter.Row.Luid))
        {
            return NetworkChangeBlock.AddressAssigned;
        }

        return snapshot.Routes.Rows.Any(r => r.Luid == adapter.Row.Luid) ? NetworkChangeBlock.RouteInUse : null;
    }

    /// <summary>
    /// The provider part of the rules, and for enabling the only part: <paramref name="adapter"/> is the same adapter in the
    /// IP Helper table, or null when it is not listed there.
    /// </summary>
    public static NetworkChangeBlock? CheckProvider(ProviderAdapter provider, NetworkAdapter? adapter, bool enable, NetworkSnapshot snapshot)
    {
        if (!provider.HardwareInterface || !provider.ConnectorPresent || provider.Virtual || provider.Hidden || provider.NdisPhysicalMedium is not (14 or 9))
        {
            return NetworkChangeBlock.Unsupported;
        }

        if (!enable)
        {
            if (adapter is null)
            {
                return NetworkChangeBlock.InformationIncomplete;
            }

            if (provider.NetLuid != adapter.Row.Luid || provider.InterfaceIndex != adapter.Row.Index)
            {
                return NetworkChangeBlock.IdentityMismatch;
            }

            return provider.MediaConnectState != 2 ? NetworkChangeBlock.ActiveAdapter : null;
        }

        // Enabling a disabled adapter: NDIS may report it with other flags, another index (both documents say the index can
        // change on disable/enable) or not at all, so kind and identity come from the provider (GUID, and the LUID when the
        // interface is listed); IP Helper contributes the stack relations only. Without a known index the stack is not
        // checked: re-enabling must stay possible for an adapter Sysacta itself disabled.
        if (!snapshot.Interfaces.IsComplete || !snapshot.Stack.IsComplete)
        {
            return NetworkChangeBlock.InformationIncomplete;
        }

        if (adapter is not null && adapter.Row.Luid != provider.NetLuid)
        {
            return NetworkChangeBlock.IdentityMismatch;
        }

        var index = adapter?.Row.Index ?? provider.InterfaceIndex;
        return index != 0 && HasDependency(index, snapshot) ? NetworkChangeBlock.DependencyPresent : null;
    }

    /// <summary>MSFT_NetAdapter.State: 2 started → enabled, 3 disabled → disabled, anything else unknown.</summary>
    public static NetworkAdminState AdminStateOf(ProviderAdapter provider) => provider.State switch
    {
        2 => NetworkAdminState.Enabled,
        3 => NetworkAdminState.Disabled,
        _ => NetworkAdminState.Unknown,
    };

    /// <summary>
    /// True when the interface is stacked with another non-filter interface (a team, a multiplexor, a tunnel over it, ...).
    /// NDIS lightweight filter interfaces sit in the same stack above every miniport, so the walk passes through them; any
    /// other neighbour, including an index missing from the interface table, counts as a dependency.
    /// </summary>
    internal static bool HasDependency(uint index, NetworkSnapshot snapshot)
    {
        var filters = snapshot.Interfaces.Rows.Where(r => r.FilterInterface).Select(r => r.Index).ToHashSet();
        var seen = new HashSet<uint> { index };
        var pending = new Queue<uint>([index]);
        while (pending.TryDequeue(out var current))
        {
            foreach (var link in snapshot.Stack.Rows)
            {
                var other = link.HigherIndex == current ? link.LowerIndex : link.LowerIndex == current ? link.HigherIndex : 0;
                if (other == 0 || !seen.Add(other))
                {
                    continue;
                }

                if (!filters.Contains(other))
                {
                    return true;
                }

                pending.Enqueue(other);
            }
        }

        return false;
    }
}
