using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Sysacta.App.ViewModels;
using Sysacta.App.Views;
using Sysacta.Core.Auditing;
using Sysacta.Core.Security;
using Sysacta.Core.Tools.Audio;
using Wpf.Ui.Appearance;

namespace Sysacta.App.Tests;

/// <summary>
/// Renders every page with sample data to PNG (light and dark) so layout problems can be reviewed without a desktop.
/// Writes only when SYSACTA_SCREENSHOT_DIR is set (CI uploads them with the test results). No database is used.
/// </summary>
public class PageScreenshots
{
    [Fact]
    public void Render_pages()
    {
        var directory = Environment.GetEnvironmentVariable("SYSACTA_SCREENSHOT_DIR");
        Assert.SkipWhen(string.IsNullOrEmpty(directory), "Set SYSACTA_SCREENSHOT_DIR to write page screenshots.");
        Directory.CreateDirectory(directory!);

        UiThread.Run(() =>
        {
            foreach (var theme in new[] { ApplicationTheme.Light, ApplicationTheme.Dark })
            {
                ApplicationThemeManager.Apply(theme, Wpf.Ui.Controls.WindowBackdropType.None, false);
                foreach (var (name, page) in Pages())
                {
                    Save(page, Path.Combine(directory!, $"{name}-{theme.ToString().ToLowerInvariant()}.png"));
                }
            }

            ApplicationThemeManager.Apply(ApplicationTheme.Light, Wpf.Ui.Controls.WindowBackdropType.None, false);
        });
    }

    private static IEnumerable<(string, Page)> Pages()
    {
        var now = DateTimeOffset.Now;

        var overview = new OverviewViewModel(null!) { ServerStatus = "実行中", IsRunning = true, EndpointUrl = "http://127.0.0.1:8765/mcp", ToolSummary = "2 / 2 ツールが公開可能", ClientSummary = "有効 3 / 登録 4", Activity = "処理中のHTTPリクエスト 0 件 / 実行・待機中のツール呼び出し 0 件", HasRecentEvents = true };
        foreach (var e in SampleEvents(now))
        {
            overview.RecentEvents.Add(new AuditRow(e));
        }

        yield return ("overview", new OverviewPage(overview));
        yield return ("connection", new ConnectionPage(new ConnectionViewModel(null!) { Port = 8765, ConfigJson = ConnectionViewModel.ConfigFor("http://127.0.0.1:8765/mcp", "YOUR_TOKEN"), AllowedHosts = "my-pc.tailnet-name.ts.net" }));

        var tools = new ToolsViewModel(null!);
        tools.Categories.Add(new ToolCategoryGroup("オーディオ", [new ToolItem(tools, new AudioGetStateTool(null!, null!), true), new ToolItem(tools, new AudioSetVolumeTool(null!, null!), true)]));
        yield return ("tools", new ToolsPage(tools));

        var clients = new ClientsViewModel(null!) { HasClients = true };
        foreach (var i in Enumerable.Range(1, 12))
        {
            clients.Clients.Add(new ClientItem(new ClientRecord($"id{i}", i == 1 ? "Claude Desktop (とても長いクライアント名の表示確認用)" : $"agent-{i}", now.AddDays(-i), i % 3 == 0 ? now.AddDays(30) : null, i == 4 ? now : null, i % 2 == 0 ? now.AddHours(-i) : null)));
        }

        yield return ("clients", new ClientsPage(clients));

        var devices = new DevicesViewModel(null!);
        devices.Show(SampleDevices(), [new("desk", "{0.0.0.00000000}.{speakers}"), new("old-headset", "{0.0.0.00000000}.{gone}")]);
        devices.Selected = devices.Devices[0];
        devices.SelectedAlias = "desk";
        yield return ("devices", new DevicesPage(devices));

        var logs = new LogsViewModel(null!) { RetentionDays = 30, HasEvents = true, ResultSummary = "46 件" };
        foreach (var e in SampleEvents(now))
        {
            logs.Events.Add(new AuditRow(e));
        }

        yield return ("logs", new LogsPage(logs));
        yield return ("general", new GeneralPage(new GeneralViewModel(null!, () => null!)));
    }

    private static AudioDeviceListData SampleDevices() => new(
        [
            new("{0.0.0.00000000}.{speakers}", "スピーカー (とても長いオーディオデバイス名の表示確認用 High Definition Audio)", AudioDirection.Output, AudioDeviceState.Active, true, [AudioRole.Console, AudioRole.Multimedia], 40, false, ["desk"], ["audio_get_state", "audio_set_volume", "audio_set_mute", "audio_list_sessions"], null),
            new("{0.0.1.00000000}.{mic}", "マイク", AudioDirection.Input, AudioDeviceState.Unplugged, false, [], null, null, [], ["audio_get_state"], null),
        ],
        2,
        0,
        false);

    private static IEnumerable<AuditEvent> SampleEvents(DateTimeOffset now) =>
        Enumerable.Range(0, 8).Select(i => new AuditEvent
        {
            RequestId = $"r{i}",
            Timestamp = now.AddMinutes(-i),
            ClientName = i % 3 == 0 ? null : "agent-2",
            ToolName = i % 3 == 0 ? null : "audio_set_volume",
            Source = i % 3 == 0 ? "http" : "mcp",
            Outcome = i % 3 == 0 ? AuditOutcome.AuthFailed : AuditOutcome.Success,
            ErrorCode = i % 3 == 0 ? "MissingCredentials" : null,
        });

    private static void Save(Page page, string path)
    {
        const int width = 1100, height = 760;
        // A Page can only be parented by a Window or Frame; render its content with the same DataContext instead.
        var content = (UIElement)page.Content;
        page.Content = null;
        var host = new Border { Child = content, DataContext = page.DataContext, Width = width, Height = height };
        host.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        host.Child = null;
        page.Content = content;
    }
}
