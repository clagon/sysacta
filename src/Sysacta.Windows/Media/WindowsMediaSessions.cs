using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Media;
using Windows.Media.Control;
using WinRT;

namespace Sysacta.Windows.Media;

/// <summary>GSMTC adapter. A list change invalidates every issued ID, including surviving sessions.</summary>
public sealed partial class WindowsMediaSessions : IMediaSessions, IDisposable
{
    private const int MaxSearch = 256;
    private const int MaxResponse = 64;
    private readonly object _gate = new();
    private readonly Dictionary<nint, Entry> _entries = [];
    private Task<GlobalSystemMediaTransportControlsSessionManager>? _initialization;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _generation;
    private long _reconciledGeneration;
    private bool _disposed;

    private sealed record Entry(string Id, nint Identity, GlobalSystemMediaTransportControlsSession Session);
    private sealed record Snapshot(IReadOnlyList<Entry> Entries, nint Current, bool CurrentAmbiguous, long Generation);

    public async Task<MediaSessionList> ListAsync(CancellationToken cancellationToken)
    {
        var manager = await ManagerAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Capture(manager);
        var entries = new List<MediaSessionEntry>();
        var failed = 0;
        foreach (var entry in snapshot.Entries.Take(MaxResponse))
        {
            try
            {
                var playback = entry.Session.GetPlaybackInfo();
                entries.Add(new(entry.Id, IsCurrent(snapshot, entry), Status(playback.PlaybackStatus), Controls(playback.Controls)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                entries.Add(new(entry.Id, IsCurrent(snapshot, entry), MediaPlaybackStatus.Unknown, null));
            }
        }

        EnsureGeneration(snapshot.Generation);
        return new(entries, snapshot.Entries.Count, failed, snapshot.Entries.Count > MaxResponse);
    }

    public async Task<MediaSessionTarget> ResolveAsync(string? sessionId, CancellationToken cancellationToken)
    {
        var manager = await ManagerAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = Capture(manager);
        if (sessionId is null && snapshot.CurrentAmbiguous)
        {
            throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The current media session cannot be matched safely.");
        }
        var entry = sessionId is null
            ? snapshot.Entries.FirstOrDefault(e => e.Identity == snapshot.Current && snapshot.Current != 0)
            : snapshot.Entries.FirstOrDefault(e => e.Id == sessionId);
        EnsureGeneration(snapshot.Generation);
        if (entry is null)
        {
            throw new ToolFailureException(sessionId is null ? ErrorCodes.NoActiveSession : ErrorCodes.TargetNotFound,
                sessionId is null ? "There is no current media session." : "The media session is no longer available.");
        }

        // This key is internal to ResourceLocks and never appears in the response or audit log.
        return new(entry.Id, entry.Identity.ToString("x", System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task<MediaState> ReadAsync(string sessionId, bool requireCurrent, CancellationToken cancellationToken)
    {
        var manager = await ManagerAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var initial = Capture(manager);
        var entry = Find(initial, sessionId, requireCurrent);
        string? title = null;
        string? artist = null;
        string? fingerprint = null;
        try
        {
            // Do not abandon the native operation: the executor retains its admission slot until it finishes.
            var properties = await entry.Session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            title = SafeText(properties?.Title);
            artist = SafeText(properties?.Artist);
            fingerprint = Fingerprint(properties?.Title, properties?.Artist);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Metadata is optional; keep the session visible when only metadata is unavailable.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var latest = Capture(manager);
        var same = Find(latest, sessionId, requireCurrent);
        if (same.Identity != entry.Identity || latest.Generation != initial.Generation)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The media session changed during the read.");
        }

        var playback = same.Session.GetPlaybackInfo();
        var timeline = same.Session.GetTimelineProperties();
        EnsureGeneration(latest.Generation);
        return new(sessionId, IsCurrent(latest, same), Status(playback.PlaybackStatus), Controls(playback.Controls),
            title, artist, Milliseconds(timeline.Position), Milliseconds(timeline.StartTime), Milliseconds(timeline.EndTime),
            MinMilliseconds(timeline.MinSeekTime), Milliseconds(timeline.MaxSeekTime), timeline.LastUpdatedTime, fingerprint, timeline.Position.Ticks);
    }

    public Func<Task<bool>> Prepare(string sessionId, bool requireCurrent, MediaOperation operation, long? positionTicks)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = Volatile.Read(ref _generation);
            if (generation != _reconciledGeneration)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "The media session changed before the operation.");
            }

            var entry = _entries.Values.FirstOrDefault(e => e.Id == sessionId)
                ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, "The media session is no longer available.");
            if (requireCurrent)
            {
                var current = _manager?.GetCurrentSession();
                if (current is null)
                {
                    throw new ToolFailureException(ErrorCodes.TargetChanged, "The current media session changed.");
                }

                var identity = Identity(current);
                try
                {
                    if (identity != entry.Identity || Volatile.Read(ref _generation) != generation)
                    {
                        throw new ToolFailureException(ErrorCodes.TargetChanged, "The current media session changed.");
                    }
                }
                finally
                {
                    Marshal.Release(identity);
                }
            }

            EnsureGeneration(generation);
            // Calling the WinRT method starts the operation; no task or native call is created during preparation.
            return operation switch
            {
                MediaOperation.Play => () => entry.Session.TryPlayAsync().AsTask(),
                MediaOperation.Pause => () => entry.Session.TryPauseAsync().AsTask(),
                MediaOperation.Stop => () => entry.Session.TryStopAsync().AsTask(),
                MediaOperation.NextTrack => () => entry.Session.TrySkipNextAsync().AsTask(),
                MediaOperation.PreviousTrack => () => entry.Session.TrySkipPreviousAsync().AsTask(),
                MediaOperation.Seek when positionTicks is not null => () => entry.Session.TryChangePlaybackPositionAsync(positionTicks.Value).AsTask(),
                _ => throw new InvalidOperationException("Invalid media operation."),
            };
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> ManagerAsync(CancellationToken cancellationToken)
    {
        Task<GlobalSystemMediaTransportControlsSessionManager> initialization;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            initialization = _initialization ??= Task.Run(InitializeAsync);
        }

        return await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<GlobalSystemMediaTransportControlsSessionManager> InitializeAsync()
    {
        try
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask().ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                manager.SessionsChanged += OnSessionsChanged;
                _manager = manager;
            }
            return manager;
        }
        catch
        {
            lock (_gate) { _initialization = null; }
            throw;
        }
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) =>
        Interlocked.Increment(ref _generation);

    private Snapshot Capture(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var generation = Volatile.Read(ref _generation);
            if (generation != _reconciledGeneration)
            {
                ClearEntries();
                _reconciledGeneration = generation;
            }

            var sessions = manager.GetSessions();
            if (sessions.Count > MaxSearch)
            {
                ClearEntries();
                throw new ToolFailureException(ErrorCodes.Busy, "Too many media sessions to identify safely.");
            }

            var seen = new HashSet<nint>();
            var entries = new List<Entry>(sessions.Count);
            try
            {
                foreach (var session in sessions)
                {
                    var identity = Identity(session);
                    if (!seen.Add(identity))
                    {
                        Marshal.Release(identity);
                        throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "Media session identity is ambiguous.");
                    }

                    if (!_entries.TryGetValue(identity, out var entry))
                    {
                        entry = new("m_" + Guid.NewGuid().ToString("N"), identity, session);
                        _entries.Add(identity, entry);
                    }
                    else
                    {
                        Marshal.Release(identity);
                    }
                    entries.Add(entry);
                }

                foreach (var stale in _entries.Keys.Where(key => !seen.Contains(key)).ToList())
                {
                    Marshal.Release(stale);
                    _entries.Remove(stale);
                }

                var current = manager.GetCurrentSession();
                nint currentIdentity = 0;
                var currentAmbiguous = false;
                if (current is not null)
                {
                    currentIdentity = Identity(current);
                    Marshal.Release(currentIdentity);
                    if (!seen.Contains(currentIdentity))
                    {
                        // GSMTC can return a distinct COM object for GetCurrentSession; AUMID is not a session ID.
                        currentIdentity = 0;
                        currentAmbiguous = true;
                    }
                }

                EnsureGeneration(generation);
                return new(entries, currentIdentity, currentAmbiguous, generation);
            }
            catch
            {
                ClearEntries();
                throw;
            }
        }
    }

    private static bool? IsCurrent(Snapshot snapshot, Entry entry) => snapshot.CurrentAmbiguous ? null : entry.Identity == snapshot.Current;

    private static Entry Find(Snapshot snapshot, string id, bool requireCurrent)
    {
        if (requireCurrent && snapshot.CurrentAmbiguous)
        {
            throw new ToolFailureException(ErrorCodes.AmbiguousTarget, "The current media session cannot be matched safely.");
        }
        var entry = snapshot.Entries.FirstOrDefault(e => e.Id == id)
            ?? throw new ToolFailureException(requireCurrent ? ErrorCodes.TargetChanged : ErrorCodes.TargetNotFound, "The selected media session is no longer available.");
        if (requireCurrent && entry.Identity != snapshot.Current)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The current media session changed.");
        }
        return entry;
    }

    private void EnsureGeneration(long generation)
    {
        if (Volatile.Read(ref _generation) != generation)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The media sessions changed during the request.");
        }
    }

    private static nint Identity(GlobalSystemMediaTransportControlsSession session)
    {
        var source = ((IWinRTObject)session).NativeObject.ThisPtr;
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(source, in iid, out var identity));
        return identity;
    }

    private static MediaPlaybackStatus Status(GlobalSystemMediaTransportControlsSessionPlaybackStatus status) => status switch
    {
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => MediaPlaybackStatus.Closed,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => MediaPlaybackStatus.Opened,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => MediaPlaybackStatus.Changing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => MediaPlaybackStatus.Stopped,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => MediaPlaybackStatus.Playing,
        GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => MediaPlaybackStatus.Paused,
        _ => MediaPlaybackStatus.Unknown,
    };

    private static MediaControls Controls(GlobalSystemMediaTransportControlsSessionPlaybackControls controls) =>
        new(controls.IsPlayEnabled, controls.IsPauseEnabled, controls.IsStopEnabled, controls.IsNextEnabled,
            controls.IsPreviousEnabled, controls.IsPlaybackPositionEnabled);

    internal static long Milliseconds(TimeSpan value) => value.Ticks < 0 && value.Ticks % TimeSpan.TicksPerMillisecond != 0
        ? (value.Ticks / TimeSpan.TicksPerMillisecond) - 1
        : value.Ticks / TimeSpan.TicksPerMillisecond;
    internal static long MinMilliseconds(TimeSpan value) => value.Ticks > 0
        ? (value.Ticks / TimeSpan.TicksPerMillisecond) + (value.Ticks % TimeSpan.TicksPerMillisecond == 0 ? 0 : 1)
        : value.Ticks / TimeSpan.TicksPerMillisecond;

    internal static string? Fingerprint(string? title, string? artist)
    {
        if (title is null && artist is null || title?.Length > 4096 || artist?.Length > 4096) return null;
        // Length prefixes prevent two different pairs from producing the same input.
        var text = $"{title?.Length ?? -1}:{title}{artist?.Length ?? -1}:{artist}";
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
    }

    internal static string? SafeText(string? value)
    {
        if (value is null || value.Length > 4096 || string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        // Relative paths and embedded URLs are private too; prefer omission to leaking a path.
        if (cleaned.IndexOfAny(['/', '\\']) >= 0 || Uri.TryCreate(cleaned, UriKind.Absolute, out _) || SensitiveText().IsMatch(cleaned)) return null;
        var result = new string(cleaned.Take(256).ToArray());
        return result.Length == 0 ? null : result;
    }

    [GeneratedRegex(@"(?i)(?:(?:^|\s)[a-z]:|\b(?:file|mailto|data|javascript|http|https|ftp|smb):)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveText();

    private void ClearEntries()
    {
        foreach (var identity in _entries.Keys) Marshal.Release(identity);
        _entries.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            ClearEntries();
        }
    }
}
