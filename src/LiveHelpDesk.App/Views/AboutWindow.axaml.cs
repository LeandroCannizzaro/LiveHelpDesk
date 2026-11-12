using System.Diagnostics;
using Avalonia.Controls;
using LiveHelpDesk.App.ViewModels;

namespace LiveHelpDesk.App.Views;

/// <summary>"Informazioni su LiveHelpDesk": who makes it, the manual, and the same update controls as Impostazioni.</summary>
public partial class AboutWindow : Window
{
    private const string CompanyUrl = "http://www.qgs.eu";
    private const string ManualUrl = "https://leandrocannizzaro.github.io/LiveHelpDesk/";

    public AboutWindow()
    {
        InitializeComponent();
        CompanySiteButton.Click += (_, _) => OpenUrl(CompanyUrl);
        ManualButton.Click += (_, _) => OpenUrl(ManualUrl);
        CloseButton.Click += (_, _) => Close();
    }

    /// <summary>Shares the same update checker and current version already shown in Impostazioni.</summary>
    public static Task ShowAsync(Window owner, UpdatesViewModel updates) =>
        new AboutWindow { Icon = owner.Icon, DataContext = updates }.ShowDialog(owner);

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
}
