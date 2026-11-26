namespace Sysacta.Core.Tools.Audio;

/// <summary>
/// Experimental OS adapter contract for changing the session-wide default audio endpoint.
/// Implementations live under Sysacta.Windows/Experimental and must not be mixed with standard audio adapters.
/// </summary>
public interface IAudioDefaultEndpointWriter
{
    /// <summary>Cheap, cached, side-effect-free capability check. Must never throw.</summary>
    ToolAvailability GetAvailability();

    /// <summary>Prepares the underlying policy interface without changing any state. Throws <see cref="ToolFailureException"/> on failure.</summary>
    IAudioDefaultEndpointChange Open();
}

/// <summary>One opened policy-config handle used for a single tool execution.</summary>
public interface IAudioDefaultEndpointChange : IDisposable
{
    /// <summary>Assigns <paramref name="endpointId"/> as the default endpoint for <paramref name="role"/>.</summary>
    void SetDefault(string endpointId, AudioRole role);
}
