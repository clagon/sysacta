using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Sysacta.Core.Tools;
using Sysacta.Core.Tools.Media;
using Windows.Media.Control;
using WinRT;

namespace Sysacta.Windows.Media;

/// <summary>GSMTC adapter. Session IDs remain bound to retained COM identities until those sessions disappear.</summary>
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
    private bool _eventTrackingFailed;

    private sealed class Entry(string id, nint identity, GlobalSystemMediaTransportControlsSession session)
    {
        private long _mediaRevision;
        public string Id { get; } = id;
        public nint Identity { get; } = identity;
        public GlobalSystemMediaTransportControlsSession Session { get; } = session;
        public long MediaRevision => Volatile.Read(ref _mediaRevision);
        public void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
            Interlocked.Increment(ref _mediaRevision);
    }
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
            string? appLabel;
            try
            {
                appLabel = SafeAppLabel(entry.Session.SourceAppUserModelId);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                appLabel = null;
            }

            try
            {
                var playback = entry.Session.GetPlaybackInfo();
                entries.Add(new(entry.Id, IsCurrent(snapshot, entry), Status(playback.PlaybackStatus), Controls(playback.Controls), appLabel));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed++;
                entries.Add(new(entry.Id, IsCurrent(snapshot, entry), MediaPlaybackStatus.Unknown, null, appLabel));
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
        var mediaRevision = entry.MediaRevision;
        string? title = null;
        string? artist = null;
        string? fingerprint = null;
        try
        {
            // Do not abandon the native operation: the executor retains its admission slot until it finishes.
            var properties = await entry.Session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            var rawTitle = properties?.Title;
            var rawArtist = properties?.Artist;
            title = SafeText(rawTitle);
            artist = SafeText(rawArtist);
            fingerprint = Fingerprint(rawTitle, rawArtist);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Metadata is optional; keep the session visible when only metadata is unavailable.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var latest = Capture(manager);
        var same = Find(latest, sessionId, requireCurrent);
        if (same.Identity != entry.Identity || latest.Generation != initial.Generation || same.MediaRevision != mediaRevision)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The media session changed during the read.");
        }

        var playback = same.Session.GetPlaybackInfo();
        var timeline = same.Session.GetTimelineProperties();
        EnsureGeneration(latest.Generation);
        EnsureMediaRevision(same, mediaRevision);
        return new(sessionId, IsCurrent(latest, same), Status(playback.PlaybackStatus), Controls(playback.Controls),
            title, artist, Milliseconds(timeline.Position), Milliseconds(timeline.StartTime), Milliseconds(timeline.EndTime),
            MinMilliseconds(timeline.MinSeekTime), Milliseconds(timeline.MaxSeekTime), timeline.LastUpdatedTime, fingerprint, timeline.Position.Ticks,
            timeline.MinSeekTime.Ticks, timeline.MaxSeekTime.Ticks, mediaRevision);
    }

    public async Task<Func<Task<bool>>> PrepareAsync(string sessionId, bool requireCurrent, MediaOperation operation,
        long? positionTicks, MediaState expected, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        GlobalSystemMediaTransportControlsSessionManager manager;
        Entry entry;
        long generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureEventTracking();
            manager = _manager ?? throw new ToolFailureException(ErrorCodes.OsApiError, "Media sessions are unavailable.");
            generation = Volatile.Read(ref _generation);
            if (generation != _reconciledGeneration)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "The media session changed before the operation.");
            }

            entry = _entries.Values.FirstOrDefault(e => e.Id == sessionId)
                ?? throw new ToolFailureException(ErrorCodes.TargetNotFound, "The media session is no longer available.");
        }

        string? finalFingerprint = null;
        var finalFingerprintAvailable = false;
        if (operation == MediaOperation.Seek)
        {
            try
            {
                var properties = await entry.Session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
                finalFingerprint = Fingerprint(properties?.Title, properties?.Artist);
                finalFingerprintAvailable = finalFingerprint is not null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Continue only if the earlier read had no fingerprint to lose.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureEventTracking();
            EnsureGeneration(generation);
            if (!_entries.TryGetValue(entry.Identity, out var live) || !ReferenceEquals(entry, live) || entry.Id != sessionId)
            {
                throw new ToolFailureException(ErrorCodes.TargetNotFound, "The media session is no longer available.");
            }

            var playback = entry.Session.GetPlaybackInfo();
            if (!Controls(playback.Controls).Supports(operation))
            {
                throw new ToolFailureException(ErrorCodes.NotSupported, "The media session no longer supports this operation.");
            }
            if (Status(playback.PlaybackStatus) != expected.PlaybackStatus)
            {
                throw new ToolFailureException(ErrorCodes.TargetChanged, "The media playback state changed before the operation.");
            }
            if (operation == MediaOperation.Seek)
            {
                EnsureMediaRevision(entry, expected.MediaRevision);
                if (!MatchesKnownFingerprint(expected.MetadataFingerprint, finalFingerprint, finalFingerprintAvailable))
                {
                    throw new ToolFailureException(ErrorCodes.TargetChanged, "The media item could not be revalidated before the operation.");
                }
                var timeline = entry.Session.GetTimelineProperties();
                if (expected.MinSeekTicks != timeline.MinSeekTime.Ticks || expected.MaxSeekTicks != timeline.MaxSeekTime.Ticks ||
                    expected.StartMs != Milliseconds(timeline.StartTime) || expected.EndMs != Milliseconds(timeline.EndTime) ||
                    positionTicks is null || positionTicks < timeline.MinSeekTime.Ticks || positionTicks > timeline.MaxSeekTime.Ticks)
                {
                    throw new ToolFailureException(ErrorCodes.TargetChanged, "The media item or seek range changed before the operation.");
                }
            }

            if (requireCurrent)
            {
                var current = manager.GetCurrentSession();
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
            if (operation == MediaOperation.Seek) EnsureMediaRevision(entry, expected.MediaRevision);
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
            EnsureEventTracking();
            var generation = Volatile.Read(ref _generation);
            if (generation != _reconciledGeneration)
            {
                _reconciledGeneration = generation;
                EnsureEventTracking();
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
                        try
                        {
                            session.MediaPropertiesChanged += entry.OnMediaPropertiesChanged;
                            _entries.Add(identity, entry);
                        }
                        catch
                        {
                            ReleaseEntry(entry);
                            throw;
                        }
                    }
                    else
                    {
                        Marshal.Release(identity);
                    }
                    entries.Add(entry);
                }

                foreach (var stale in _entries.Keys.Where(key => !seen.Contains(key)).ToList())
                {
                    var removed = _entries[stale];
                    _entries.Remove(stale);
                    ReleaseEntry(removed);
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

    private static void EnsureMediaRevision(Entry entry, long? expected)
    {
        if (expected is null || entry.MediaRevision != expected)
        {
            throw new ToolFailureException(ErrorCodes.TargetChanged, "The media item changed before the operation.");
        }
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

    internal static string? SafeAppLabel(string? value) =>
        value is { Length: > 0 and <= 256 } && SafeAppLabelPattern().IsMatch(value) ? value : null;

    internal static bool MatchesKnownFingerprint(string? expected, string? actual, bool readSucceeded) =>
        expected is null || readSucceeded && actual is not null && expected == actual;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._!+-]{0,255}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeAppLabelPattern();

    [GeneratedRegex(@"(?i)(?:(?:^|\s)[a-z]:|\b(?:file|mailto|data|javascript|http|https|ftp|smb):)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveText();

    private void EnsureEventTracking()
    {
        if (_eventTrackingFailed)
        {
            throw new ToolFailureException(ErrorCodes.OsApiError, "Media session change tracking is unavailable.");
        }
    }

    private void ReleaseEntry(Entry entry)
    {
        try
        {
            entry.Session.MediaPropertiesChanged -= entry.OnMediaPropertiesChanged;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Stop accepting new sessions if a subscription may still be held by the OS.
            _eventTrackingFailed = true;
        }
        finally
        {
            Marshal.Release(entry.Identity);
        }
    }

    private void ClearEntries()
    {
        foreach (var entry in _entries.Values) ReleaseEntry(entry);
        _entries.Clear();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_manager is not null) _manager.SessionsChanged -= OnSessionsChanged;
            }
            finally
            {
                ClearEntries();
            }
        }
    }
}
