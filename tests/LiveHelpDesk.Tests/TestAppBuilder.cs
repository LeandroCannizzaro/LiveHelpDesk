using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;
using LiveHelpDesk.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace LiveHelpDesk.Tests;

/// <summary>A bare Avalonia application for rendering views off-screen; the real <c>App</c> would start the tray and the window.</summary>
public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
