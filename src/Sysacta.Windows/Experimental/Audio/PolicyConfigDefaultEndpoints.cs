using System.Runtime.InteropServices;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Audio;

namespace Sysacta.Windows.Experimental.Audio;

/// <summary>
/// Isolated adapter for changing the session-wide default audio endpoint via the undocumented Windows COM interface
/// <c>IPolicyConfig</c> (<c>CPolicyConfigClient</c>).
/// <para>
/// References (contract verification only; no third-party code copied):
/// - frgnca/AudioDeviceCmdlets (MIT, commit 26d08d30e9d804c016c994117f9b038b7d50da95): CLSID 870af99c-171d-4f9e-af0d-e63df40c2bc9,
///   IID f8679f50-850a-41cf-9c72-430f290290c8, SetDefaultEndpoint at vtable slot 13.
/// - File-New-Project/EarTrumpet historical IPolicyConfig layout (prior to per-app IAudioPolicyConfigFactory).
/// </para>
/// <para>
/// Risk: Because this COM interface is undocumented, a future Windows update may change or remove it (potentially
/// failing with E_NOINTERFACE or crashing on ABI mismatch). Do not add other methods or fallback interfaces here.
/// </para>
/// </summary>
public sealed class PolicyConfigDefaultEndpoints : IAudioDefaultEndpointWriter
{
    private static readonly Guid PolicyConfigClientClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    private const int SOk = 0;
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int RegdbEClassNotReg = unchecked((int)0x80040154);
    private const int ENotFound = unchecked((int)0x80070490);
    private const int EInvalidArg = unchecked((int)0x80070057);
    private const int DeviceInvalidated = unchecked((int)0x88890004);

    private readonly object _availabilityLock = new();
    private ToolAvailability? _cachedAvailability;

    public ToolAvailability GetAvailability()
    {
        lock (_availabilityLock)
        {
            if (_cachedAvailability is not null)
            {
                return _cachedAvailability;
            }

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                _cachedAvailability = ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Requires Windows 11.");
                return _cachedAvailability;
            }

            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                _cachedAvailability = ToolAvailability.Unavailable(ErrorCodes.NotSupported, "Only x64 is supported.");
                return _cachedAvailability;
            }

            var (availability, cacheable) = Thread.CurrentThread.GetApartmentState() == ApartmentState.MTA
                ? ProbeAvailability()
                : Task.Run(ProbeAvailability).GetAwaiter().GetResult();

            if (cacheable)
            {
                _cachedAvailability = availability;
            }

            return availability;
        }
    }

    public IAudioDefaultEndpointChange Open()
    {
        var (instance, policyConfig, error) = CreateInstance();
        if (policyConfig is not null && instance is not null)
        {
            return new Handle(instance, policyConfig);
        }

        if (instance is not null && Marshal.IsComObject(instance))
        {
            Marshal.FinalReleaseComObject(instance);
        }

        throw error ?? new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface is not available.");
    }

    private static (ToolAvailability Availability, bool Cacheable) ProbeAvailability()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid, throwOnError: false);
            if (type is null)
            {
                return (ToolAvailability.Unavailable(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."), true);
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IPolicyConfig)
            {
                return (ToolAvailability.Unavailable(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."), true);
            }

            return (ToolAvailability.Available, true);
        }
        catch (COMException ex) when (ex.HResult is RegdbEClassNotReg or ENoInterface)
        {
            return (ToolAvailability.Unavailable(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."), true);
        }
        catch (InvalidCastException)
        {
            return (ToolAvailability.Unavailable(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."), true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Transient failures are not cached as permanently unsupported; Open() will report a structured OS_API_ERROR.
            return (ToolAvailability.Available, false);
        }
        finally
        {
            if (instance is not null && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    private static (object? Instance, IPolicyConfig? PolicyConfig, ToolFailureException? Error) CreateInstance()
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(PolicyConfigClientClsid, throwOnError: false);
            if (type is null)
            {
                return (null, null, new ToolFailureException(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."));
            }

            instance = Activator.CreateInstance(type);
            if (instance is not IPolicyConfig policyConfig)
            {
                return (instance, null, new ToolFailureException(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."));
            }

            return (instance, policyConfig, null);
        }
        catch (COMException ex) when (ex.HResult is RegdbEClassNotReg or ENoInterface)
        {
            return (instance, null, new ToolFailureException(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."));
        }
        catch (COMException)
        {
            return (instance, null, new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error."));
        }
        catch (InvalidCastException)
        {
            return (instance, null, new ToolFailureException(ErrorCodes.NotSupported, "The Windows audio policy interface is not available."));
        }
    }

    private sealed class Handle(object instance, IPolicyConfig policyConfig) : IAudioDefaultEndpointChange
    {
        private int _disposed;

        public void SetDefault(string endpointId, AudioRole role)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var nativeRole = role switch
            {
                AudioRole.Console => 0,
                AudioRole.Multimedia => 1,
                AudioRole.Communications => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(role)),
            };

            try
            {
                var hr = policyConfig.SetDefaultEndpoint(endpointId, nativeRole);
                switch (hr)
                {
                    case SOk:
                        return;
                    case ENotFound:
                    case EInvalidArg:
                        throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
                    case DeviceInvalidated:
                        throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
                    default:
                        throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error.");
                }
            }
            catch (COMException ex) when (ex.HResult == DeviceInvalidated)
            {
                throw new ToolFailureException(ErrorCodes.DeviceDisconnected, "The audio device was removed or disabled.");
            }
            catch (COMException ex) when (ex.HResult is ENotFound or EInvalidArg)
            {
                throw new ToolFailureException(ErrorCodes.TargetNotFound, "The specified audio device was not found.");
            }
            catch (COMException)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface returned an error.");
            }
            catch (InvalidCastException)
            {
                throw new ToolFailureException(ErrorCodes.OsApiError, "The Windows audio policy interface is not available.");
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0 && Marshal.IsComObject(instance))
            {
                Marshal.FinalReleaseComObject(instance);
            }
        }
    }

    // Minimal IPolicyConfig declaration (IUnknown + 10 vtable slots + SetDefaultEndpoint at slot 13).
    // Reserved0..Reserved9 are never called; they preserve the exact COM vtable layout.
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPolicyConfig
    {
        [PreserveSig] int Reserved0();
        [PreserveSig] int Reserved1();
        [PreserveSig] int Reserved2();
        [PreserveSig] int Reserved3();
        [PreserveSig] int Reserved4();
        [PreserveSig] int Reserved5();
        [PreserveSig] int Reserved6();
        [PreserveSig] int Reserved7();
        [PreserveSig] int Reserved8();
        [PreserveSig] int Reserved9();

        [PreserveSig]
        int SetDefaultEndpoint(
            [In, MarshalAs(UnmanagedType.LPWStr)] string deviceId,
            [In] int role);
    }
}
