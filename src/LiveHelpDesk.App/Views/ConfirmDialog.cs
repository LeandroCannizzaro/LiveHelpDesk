using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace LiveHelpDesk.App.Views;

/// <summary>A small modal question with two answers. Closing it any other way counts as "no".</summary>
public sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string message, string yes, string no)
    {
        Title = "LiveHelpDesk";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var yesButton = new Button { Content = yes };
        yesButton.Click += (_, _) => Close(true);

        // The safe answer is the default one, so a stray Enter does not reactivate anything.
        var noButton = new Button { Content = no, IsDefault = true, IsCancel = true };
        noButton.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 14 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { yesButton, noButton },
                },
            },
        };
    }

    public static async Task<bool> AskAsync(Window owner, string message, string yes = "Sì, riattiva", string no = "Annulla") =>
        await new ConfirmDialog(message, yes, no).ShowDialog<bool>(owner);
}
