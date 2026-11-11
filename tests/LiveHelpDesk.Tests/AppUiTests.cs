using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.App.ViewModels;
using LiveHelpDesk.App.Views;
using LiveHelpDesk.Ipc;
using Xunit;

#pragma warning disable CS0618 // Bitmap.Save(path): the replacement needs encoder options we do not care about here

namespace LiveHelpDesk.Tests;

/// <summary>
/// Off-screen rendering of the real views. Set <c>LHD_SHOTS</c> to a folder to keep the images and look at them;
/// the assertions read pixels and text, so they hold without anyone looking.
/// </summary>
public class AppUiTests
{
    internal static void Save(WriteableBitmap frame, string name)
    {
        if (Environment.GetEnvironmentVariable("LHD_SHOTS") is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            frame.Save(Path.Combine(folder, name + ".png"));
        }
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(WriteableBitmap bitmap, int x, int y)
    {
        using var framebuffer = bitmap.Lock();
        var at = framebuffer.Address + y * framebuffer.RowBytes + x * 4;
        var first = Marshal.ReadByte(at, 0);
        var second = Marshal.ReadByte(at, 1);
        var third = Marshal.ReadByte(at, 2);
        var alpha = Marshal.ReadByte(at, 3);

        // The byte order depends on the renderer: ask the bitmap instead of assuming.
        return framebuffer.Format == Avalonia.Platform.PixelFormat.Bgra8888
            ? (third, second, first, alpha)
            : (first, second, third, alpha);
    }

    private static (byte R, byte G, byte B, byte A) PixelAt(Bitmap bitmap, int x, int y)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        stream.Position = 0;
        return PixelAt(WriteableBitmap.Decode(stream), x, y);
    }

    private static IEnumerable<string> Texts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").Where(t => t.Length > 0);

    private static OverlayWindow Overlay(AttentionModel model, out OverlayViewModel vm)
    {
        vm = new OverlayViewModel(() => Task.CompletedTask, () => Task.CompletedTask, _ => { });
        vm.Update(model, T.At(1));
        var window = new OverlayWindow { DataContext = vm };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static AttentionItem Item(string key, Severity severity, string title = "Nuovo ticket #261019389", string body = "data fattura\nPriorità: Normale") =>
        new(key, title, body, severity, new Uri("https://crm.example/t/1"), T.At(0));

    [AvaloniaFact]
    public void TheOverlay_IsAmberForAttention()
    {
        var model = new AttentionModel();
        model.Add(Item("a", Severity.Attention), T.At(0));
        var window = Overlay(model, out _);

        var frame = window.CaptureRenderedFrame()!;
        Save(frame, "overlay-attention");

        var (r, g, b, a) = PixelAt(frame, 280, 18);
        Assert.InRange(r, 0xE8, 0xFF);
        Assert.InRange(g, 0x90, 0xB0);
        Assert.InRange(b, 0x00, 0x30);
        Assert.Equal(255, a);
        Assert.Contains("Nuovo ticket #261019389", Texts(window));
        Assert.Contains("data fattura", Texts(window));
        Assert.Contains("1", Texts(window));
    }

    [AvaloniaFact]
    public void TheOverlay_IsRedForCritical_AndSaysHowManyAreWaiting()
    {
        var model = new AttentionModel();
        model.Add(Item("a", Severity.Attention), T.At(0));
        model.Add(Item("b", Severity.Critical, "Ticket #261019267 senza risposta", "Richiesta Supporto BFA"), T.At(0));
        var window = Overlay(model, out _);

        var frame = window.CaptureRenderedFrame()!;
        Save(frame, "overlay-critical");

        var (r, g, b, _) = PixelAt(frame, 280, 18);
        Assert.InRange(r, 0xC8, 0xE8);
        Assert.InRange(g, 0x10, 0x48);
        Assert.InRange(b, 0x10, 0x48);
        Assert.Contains("Ticket #261019267 senza risposta", Texts(window));
        Assert.Contains("Richiesta Supporto BFA · +1 da gestire", Texts(window));
        Assert.Contains("2", Texts(window));
    }

    [AvaloniaFact]
    public void TheHeartbeat_IsAClassOnTheBadge_DrivenByTheViewModel()
    {
        var critical = new AttentionModel();
        critical.Add(Item("a", Severity.Critical), T.At(0));
        var criticalWindow = Overlay(critical, out _);
        var criticalBadge = criticalWindow.FindControl<Border>("Badge")!;
        Assert.Contains("critical", criticalBadge.Classes);
        Assert.DoesNotContain("attention", criticalBadge.Classes);
        Save(criticalWindow.CaptureRenderedFrame()!, "overlay-critical-badge");

        var attention = new AttentionModel();
        attention.Add(Item("b", Severity.Attention), T.At(0));
        var attentionWindow = Overlay(attention, out _);
        var attentionBadge = attentionWindow.FindControl<Border>("Badge")!;
        Assert.Contains("attention", attentionBadge.Classes);
        Assert.DoesNotContain("critical", attentionBadge.Classes);
    }

    [AvaloniaFact]
    public void TheOverlayButtons_AreThere()
    {
        var model = new AttentionModel();
        model.Add(Item("a", Severity.Attention), T.At(0));
        var window = Overlay(model, out _);

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToArray();

        Assert.Equal(new[] { "\u25B2", "Apri", "Riconosci", "15 min" }, buttons.Select(b => b ?? "").ToArray());
    }

    [AvaloniaFact]
    public void TheServiceView_AsksForTheLogin_ThenShowsTheCode()
    {
        var api = new FakeApi { Status = new FakeApi().Status with { State = ProfileState.NeedsLogin, UserName = null } };
        var vm = new StatusViewModel(() => api, _ => Task.CompletedTask, _ => { });
        vm.Apply(api.Status);
        var window = new Window { Width = 800, Height = 620, Content = new StatusView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Serve l'accesso a Dynamics", Texts(window));
        Assert.Contains("Accesso a Dynamics", Texts(window));
        Save(window.CaptureRenderedFrame()!, "status-needs-login");

        vm.StartLoginCommand.Execute(null);
        for (var i = 0; i < 50 && vm.LoginCode == ""; i++)
        {
            Thread.Sleep(10);
            Dispatcher.UIThread.RunJobs();
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Contains("ABC123", Texts(window));
        Save(window.CaptureRenderedFrame()!, "status-login-code");
    }

    [AvaloniaFact]
    public async Task TheServiceView_OffersToStartTheProcessOrInstallTheService_WhenNothingRuns()
    {
        var vm = new StatusViewModel(() => null, _ => Task.CompletedTask, _ => { });
        var hosting = new HostingViewModel(
            new NoService(), new MonitorProcessHost(() => throw new InvalidOperationException(), () => false), true, _ => { });
        vm.Hosting = hosting;
        hosting.Refresh(LinkState.ServiceMissing);
        await vm.RefreshAsync(LinkState.ServiceMissing);
        var window = new Window { Width = 800, Height = 620, Content = new StatusView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToArray();
        Assert.Contains("Avvia il processo", buttons);
        Assert.Contains("Installa il servizio", buttons);
        Assert.Contains("Disinstalla il servizio", buttons);
        Assert.Contains("Servizio Windows: non installato", Texts(window));
        Save(window.CaptureRenderedFrame()!, "status-hosting");
    }

    private sealed class NoService : IWindowsServiceManager
    {
        public bool IsElevated => false;

        public WindowsServiceState Query() => WindowsServiceState.NotInstalled;

        public Task<ServiceActionResult> RunAsync(ServiceAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServiceActionResult(true, "ok"));
    }
    [AvaloniaFact]
    public async Task TheServiceView_ShowsTheChannels()
    {
        var api = new FakeApi();
        var vm = new StatusViewModel(() => api, _ => Task.CompletedTask, _ => { });
        await vm.RefreshAsync(LinkState.Connected);
        var window = new Window { Width = 800, Height = 520, Content = new StatusView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("E-mail", Texts(window));
        Assert.Contains("SMS", Texts(window));
        Assert.Contains(Texts(window), t => t.StartsWith("Tutto in ordine"));
        Save(window.CaptureRenderedFrame()!, "status-running");
    }

    [AvaloniaFact]
    public async Task TheEventList_ShowsTheEvents()
    {
        var vm = new EventsViewModel(() => new FakeApi(), _ => Task.CompletedTask, _ => Task.CompletedTask);
        await vm.RefreshAsync();
        var window = new Window { Width = 900, Height = 360, Content = new EventsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("Nuovo ticket", Texts(window));
        Assert.Contains("#261000001", Texts(window));
        Assert.Contains("Errore impegno", Texts(window));
        Save(window.CaptureRenderedFrame()!, "events");
    }

    [AvaloniaFact]
    public void TheEmptyEventList_SaysThereIsNothing()
    {
        var vm = new EventsViewModel(() => null, _ => Task.CompletedTask, _ => Task.CompletedTask);
        var window = new Window { Width = 600, Height = 200, Content = new EventsView { DataContext = vm } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("In attesa del servizio…", Texts(window));
    }

    [AvaloniaTheory]
    [InlineData(TrayLook.Idle, 0x9C, 0xA3, 0xAF)]
    [InlineData(TrayLook.Ok, 0x16, 0xA3, 0x4A)]
    [InlineData(TrayLook.Attention, 0xF5, 0x9E, 0x0B)]
    [InlineData(TrayLook.Alert, 0xDC, 0x26, 0x26)]
    public void EachTrayIcon_HasItsColour(TrayLook look, int r, int g, int b)
    {
        var (pr, pg, pb, alpha) = PixelAt(TrayIconFactory.CreateBitmap(look), 5, 16);

        Assert.Equal(255, alpha);
        Assert.InRange(pr, r - 4, r + 4);
        Assert.InRange(pg, g - 4, g + 4);
        Assert.InRange(pb, b - 4, b + 4);
    }

    [AvaloniaFact]
    public void TheCorners_OfTheTrayIcon_AreTransparent()
    {
        var (_, _, _, alpha) = PixelAt(TrayIconFactory.CreateBitmap(TrayLook.Ok), 0, 0);

        Assert.Equal(0, alpha);
    }
}
