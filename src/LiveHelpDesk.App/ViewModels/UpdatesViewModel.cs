using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.App.Services;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>Settings → "Aggiornamenti": the installed version, whether a newer one is out, and the button that installs it.</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly Func<CancellationToken, Task<AvailableUpdate?>> _check;
    private readonly Func<AvailableUpdate, IProgress<double>, CancellationToken, Task> _install;
    private readonly Action<Uri> _open;
    private readonly Uri _releaseNotes;
    private AvailableUpdate? _update;

    /// <param name="releaseNotes">The release notes page on GitHub Pages; each version is an anchor "#vX.Y.Z".</param>
    public UpdatesViewModel(
        Version current, InstallKind kind, Func<CancellationToken, Task<AvailableUpdate?>> check,
        Func<AvailableUpdate, IProgress<double>, CancellationToken, Task> install, Action<Uri> open, Uri? releaseNotes = null)
    {
        CurrentVersion = current.ToString(3);
        Kind = kind;
        _check = check;
        _install = install;
        _open = open;
        _releaseNotes = releaseNotes ?? UpdateChecker.ReleaseNotesUrl(UpdateChecker.DefaultManifestUrl);
    }

    public string CurrentVersion { get; }

    public InstallKind Kind { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isAvailable;

    [ObservableProperty]
    private string _availableVersion = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand), nameof(CheckCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    /// <summary>A newer version is out and this copy knows how to install it by itself.</summary>
    public bool CanInstall => IsAvailable && !IsBusy && _update?.Package is not null;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        Status = "Controllo se c'è una versione più recente…";
        try
        {
            _update = await _check(CancellationToken.None);
            IsAvailable = _update is not null;
            AvailableVersion = _update?.Version.ToString(3) ?? "";
            Status = _update switch
            {
                null => $"Hai già la versione più recente ({CurrentVersion}).",
                { Package: null } when Kind == InstallKind.None =>
                    $"È disponibile la versione {AvailableVersion}. Questa copia non è installata: scaricala dalla pagina delle versioni.",
                { Package: null } => $"È disponibile la versione {AvailableVersion}, ma senza un pacchetto adatto a questa installazione.",
                _ => $"È disponibile la versione {AvailableVersion}. Premi «Aggiorna e riavvia» per installarla.",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or System.Text.Json.JsonException)
        {
            Status = $"Impossibile controllare gli aggiornamenti: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(CanInstall));
            InstallCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_update is not { Package: not null } update)
        {
            return;
        }

        IsBusy = true;
        Progress = 0;
        Status = $"Scarico la versione {AvailableVersion}…";
        try
        {
            await _install(update, new Progress<double>(value => Progress = value * 100), CancellationToken.None);
            Status = "Installo l'aggiornamento: LiveHelpDesk si chiude e riparte da solo.";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Status = $"Aggiornamento non riuscito: {ex.Message}";
            IsBusy = false;
        }
    }

    /// <summary>«Note di rilascio»: the notes of the version on offer, or of the one installed.</summary>
    [RelayCommand]
    private void OpenReleaseNotes() =>
        _open(_update?.Manifest.NotesUrl ?? new UriBuilder(_releaseNotes) { Fragment = $"v{_update?.Version.ToString(3) ?? CurrentVersion}" }.Uri);
}
