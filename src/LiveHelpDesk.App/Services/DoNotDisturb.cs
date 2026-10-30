using System.Runtime.InteropServices;

namespace LiveHelpDesk.App.Services;

/// <summary>Is the user in the middle of something that must not be covered (a presentation, a full-screen game)?</summary>
public interface IDoNotDisturb
{
    bool IsBusy();
}

public sealed class NeverBusy : IDoNotDisturb
{
    public bool IsBusy() => false;
}

public sealed class WindowsDoNotDisturb : IDoNotDisturb
{
    // QUERY_USER_NOTIFICATION_STATE
    private const int Busy = 2;
    private const int FullScreen = 3;
    private const int Presentation = 4;

    public bool IsBusy() => IsBusyState(Query());

    public static bool IsBusyState(int state) => state is Busy or FullScreen or Presentation;

    private static int Query() => SHQueryUserNotificationState(out var state) == 0 ? state : 5; // 5 = accepts notifications

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);
}
