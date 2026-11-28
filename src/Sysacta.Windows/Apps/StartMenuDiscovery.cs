using Sysacta.Core.Tools.Apps;
using Sysacta.Windows.Notification;
using static Sysacta.Windows.Apps.NativeMethods;

namespace Sysacta.Windows.Apps;

/// <summary>A Start Menu shortcut to an executable, for the local console only. Nothing is registered or allowed by finding it.</summary>
/// <param name="Name">The shortcut's file name without .lnk (local display only).</param>
/// <param name="Target">Expanded target path as written in the shortcut.</param>
/// <param name="HasArguments">The shortcut passes arguments: it cannot be registered as is (the user must register the exe manually).</param>
public sealed record AppCandidate(string Name, string Target, bool HasArguments, ExecutableCheck Check);

public sealed record DiscoveryResult(IReadOnlyList<AppCandidate> Candidates, bool Truncated, int SkippedFolders);

/// <summary>
/// Reads the Start Menu program folders of the user and of all users (local fixed drives only), on an explicit console
/// action. Shortcuts are only read (IPersistFile.Load): never resolved, launched or followed over the network; folders
/// that are reparse points are not entered. Limited in depth, entries, results and time.
/// </summary>
public static class StartMenuDiscovery
{
    public const int MaxDepth = 8;
    public const int MaxEntries = 2048;
    public const int MaxResults = 256;

    public static Task<DiscoveryResult> ScanAsync(IAppPlatform platform, CancellationToken cancellationToken)
    {
        var done = new TaskCompletionSource<DiscoveryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        // IShellLinkW is used on its own STA thread (it is not called from the UI thread or the thread pool).
        var thread = new Thread(() =>
        {
            try
            {
                done.SetResult(Scan(platform, cancellationToken));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        { IsBackground = true, Name = "Sysacta app discovery" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return done.Task;
    }

    private static DiscoveryResult Scan(IAppPlatform platform, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, AppCandidate>(StringComparer.OrdinalIgnoreCase);
        var skipped = 0;
        var entries = 0;
        var truncated = false;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Offline | FileAttributes.Device,
            ReturnSpecialDirectories = false,
        };

        var pending = new Queue<(string Folder, int Depth)>();
        foreach (var root in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms }.Select(Environment.GetFolderPath))
        {
            if (IsLocalFixedFolder(root))
            {
                pending.Enqueue((root, 0));
            }
            else
            {
                skipped++;
            }
        }

        while (pending.TryDequeue(out var current) && !truncated)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<FileSystemInfo> items;
            try
            {
                items = new DirectoryInfo(current.Folder).EnumerateFileSystemInfos("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++entries > MaxEntries || candidates.Count >= MaxResults)
                {
                    truncated = true;
                    break;
                }

                if (item is DirectoryInfo)
                {
                    if (current.Depth + 1 < MaxDepth)
                    {
                        pending.Enqueue((item.FullName, current.Depth + 1));
                    }

                    continue;
                }

                if (!item.Name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || ShellShortcut.ReadLink(item.FullName) is not { } link)
                {
                    continue;
                }

                // Environment variables of the user's own profile are expanded for display and checking only.
                var target = Environment.ExpandEnvironmentVariables(link.Target);
                if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || candidates.ContainsKey(target))
                {
                    continue;
                }

                var check = platform.Inspect(target, computeHash: false, cancellationToken);
                candidates[target] = new AppCandidate(Path.GetFileNameWithoutExtension(item.Name), target, link.Arguments.Length > 0, check);
            }
        }

        return new DiscoveryResult([.. candidates.Values.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)], truncated, skipped);
    }

    private static bool IsLocalFixedFolder(string folder) =>
        folder.Length > 3 && char.IsAsciiLetter(folder[0]) && folder[1] == ':' && folder[2] == '\\' && GetDriveTypeW(folder[..3]) == DriveFixed;
}
