using Sysacta.Core.Tools;
using Sysacta.Windows.Notification;

namespace Sysacta.Windows.Tests;

/// <summary>
/// Shortcut and payload handling only. Shortcuts are written to a temporary folder, which registers nothing with
/// Windows, and no notification is ever shown: Prepare is only called where it must refuse.
/// </summary>
public sealed class WindowsDesktopNotificationsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sysacta-notify-").FullName;

    private string Shortcut => Path.Combine(_dir, "Sysacta.lnk");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string Executable(string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "");
        return path;
    }

    private WindowsDesktopNotifications Register(string executable)
    {
        var notifications = new WindowsDesktopNotifications(Shortcut, executable);
        notifications.Register();
        return notifications;
    }

    [Fact]
    public void Registration_creates_a_shortcut_with_the_fixed_id_and_is_idempotent()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var exe = Executable("Sysacta.exe");

        Assert.True(Register(exe).GetAvailability().IsAvailable);
        Assert.Equal((WindowsDesktopNotifications.AppUserModelId, exe, _dir), ShellShortcut.Read(Shortcut));
        Assert.True(ShellShortcut.IsRegistration(Shortcut, exe, WindowsDesktopNotifications.AppUserModelId));

        var bytes = File.ReadAllBytes(Shortcut);
        Assert.True(Register(exe).GetAvailability().IsAvailable);
        Assert.Equal(bytes, File.ReadAllBytes(Shortcut));
    }

    [Fact]
    public void Another_existing_copy_is_a_conflict_and_a_moved_one_is_repointed_with_its_working_directory()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var first = Executable("first.exe");
        var moved = Path.Combine(Directory.CreateDirectory(Path.Combine(_dir, "moved")).FullName, "second.exe");
        File.WriteAllText(moved, "");
        Register(first);

        var bytes = File.ReadAllBytes(Shortcut);
        Assert.False(Register(moved).GetAvailability().IsAvailable);
        Assert.Equal(bytes, File.ReadAllBytes(Shortcut));

        File.Delete(first);
        Assert.True(Register(moved).GetAvailability().IsAvailable);
        Assert.Equal((WindowsDesktopNotifications.AppUserModelId, moved, Path.GetDirectoryName(moved)!), ShellShortcut.Read(Shortcut));
    }

    [Theory]
    [InlineData("empty target")]
    [InlineData("other existing target")]
    [InlineData("arguments")]
    [InlineData("other id")]
    [InlineData("removed")]
    [InlineData("unreadable")]
    public void A_registration_changed_after_startup_sends_nothing_and_is_not_repaired(string change)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var exe = Executable("Sysacta.exe");
        var notifications = Register(exe);
        Assert.True(notifications.GetAvailability().IsAvailable);

        File.Delete(Shortcut);
        const string id = WindowsDesktopNotifications.AppUserModelId;
        switch (change)
        {
            case "empty target": ShellShortcut.Create(Shortcut, "", id); break;
            case "other existing target": ShellShortcut.Create(Shortcut, Executable("other.exe"), id); break;
            case "arguments": ShellShortcut.Create(Shortcut, exe, id, "--extra"); break;
            case "other id": ShellShortcut.Create(Shortcut, exe, "Other.Application"); break;
            case "unreadable": File.WriteAllText(Shortcut, "not a shortcut"); break;
        }

        var bytes = File.Exists(Shortcut) ? File.ReadAllBytes(Shortcut) : null;
        var failure = Assert.Throws<ToolFailureException>(() => notifications.Prepare("t", "b"));
        Assert.Equal(ErrorCodes.NotSupported, failure.Code);
        Assert.DoesNotContain(_dir, failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bytes, File.Exists(Shortcut) ? File.ReadAllBytes(Shortcut) : null);

        // A restart does not accept it either (a removed shortcut is simply created again).
        Assert.Equal(change == "removed", Register(exe).GetAvailability().IsAvailable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_shortcut_that_is_not_ours_is_never_overwritten(bool realShortcut)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        if (realShortcut)
        {
            Assert.True(ShellShortcut.Ensure(Shortcut, Executable("other.exe"), "Other.Application"));
        }
        else
        {
            File.WriteAllText(Shortcut, "not a shortcut");
        }

        var bytes = File.ReadAllBytes(Shortcut);
        var notifications = Register(Executable("Sysacta.exe"));

        var availability = notifications.GetAvailability();
        Assert.Equal((false, ErrorCodes.NotSupported), (availability.IsAvailable, availability.ErrorCode));
        Assert.DoesNotContain(_dir, availability.Reason!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ErrorCodes.NotSupported, Assert.Throws<ToolFailureException>(() => notifications.Prepare("t", "b")).Code);
        Assert.Equal(bytes, File.ReadAllBytes(Shortcut));
    }

    [Fact]
    public void A_failed_or_pending_registration_sends_nothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var pending = new WindowsDesktopNotifications(Shortcut, Executable("Sysacta.exe"));
        var early = Assert.Throws<ToolFailureException>(() => pending.Prepare("t", "b"));
        Assert.Equal((ErrorCodes.OsApiError, true), (early.Code, early.Retryable));

        var failed = new WindowsDesktopNotifications(Path.Combine(_dir, "missing", "folder", "Sysacta.lnk"), Executable("Sysacta.exe"));
        failed.Register();
        Assert.False(failed.GetAvailability().IsAvailable);
        Assert.Equal(ErrorCodes.NotSupported, Assert.Throws<ToolFailureException>(() => failed.Prepare("t", "b")).Code);
    }

    [Fact]
    public void The_payload_keeps_text_as_text_and_adds_no_actions_or_attributes()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        const string title = "<toast launch=\"x\">&amp; ]]>";
        const string body = "'\"</text><actions><action activationType=\"protocol\" arguments=\"calc:\"/></actions>\n日本語 👨‍👩‍👧";

        var xml = WindowsDesktopNotifications.BuildPayload(title, body);

        var texts = xml.GetElementsByTagName("text");
        Assert.Equal(2, texts.Count);
        Assert.Equal(title, texts[0].InnerText);
        Assert.Equal(body, texts[1].InnerText);
        Assert.Empty(xml.GetElementsByTagName("actions"));
        Assert.Empty(xml.GetElementsByTagName("action"));
        Assert.Single(xml.GetElementsByTagName("toast"));
        Assert.Empty(xml.DocumentElement.Attributes);

        // The serialized form parses back to the same text.
        var reparsed = new global::Windows.Data.Xml.Dom.XmlDocument();
        reparsed.LoadXml(xml.GetXml());
        Assert.Equal(body, reparsed.GetElementsByTagName("text")[1].InnerText);
    }
}
