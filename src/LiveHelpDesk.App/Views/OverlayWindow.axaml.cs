using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace LiveHelpDesk.App.Views;

/// <summary>
/// A small always-on-top pill that must be seen but never get in the way: it does not take the keyboard focus,
/// does not appear in Alt-Tab or the taskbar, and can be dragged out of the way.
/// </summary>
public partial class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const nint WsExNoActivate = 0x08000000;
    private const nint WsExToolWindow = 0x00000080;

    public OverlayWindow()
    {
        InitializeComponent();
        Opened += (_, _) => KeepOffTheKeyboard();
        PointerPressed += OnPointerPressed;
    }

    /// <summary>Raised when the user has dragged the pill somewhere new.</summary>
    public event EventHandler? Moved;

    public void KeepOffTheKeyboard()
    {
        if (TryGetPlatformHandle() is { } handle && handle.Handle != 0)
        {
            var style = GetWindowLongPtr(handle.Handle, GwlExStyle);
            SetWindowLongPtr(handle.Handle, GwlExStyle, style | WsExNoActivate | WsExToolWindow);
        }
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Dragging by the text and the badge; the buttons keep their clicks.
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) is null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint value);
}
