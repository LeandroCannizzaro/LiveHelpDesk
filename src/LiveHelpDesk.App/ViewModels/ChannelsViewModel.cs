using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.App.Services;
using LiveHelpDesk.Ipc;

namespace LiveHelpDesk.App.ViewModels;

/// <summary>
/// "Don't disturb me on this channel from 22:00 to 07:00". The service stores allowed time windows; this turns the
/// quiet period into those windows and back. Schedules it cannot draw are kept exactly as they are.
/// </summary>
public sealed partial class QuietHoursEditor : ObservableObject
{
    private readonly ChannelSchedule? _preserved;

    public QuietHoursEditor(ChannelSchedule? schedule, int limit)
    {
        _limit = limit.ToString(CultureInfo.InvariantCulture);
        if (schedule is null || schedule.Windows.Count == 0)
        {
            _criticalPasses = schedule?.AlwaysAllowFrom == Severity.Critical || schedule is null;
            return;
        }

        _criticalPasses = schedule.AlwaysAllowFrom == Severity.Critical;
        if (TryReadQuiet(schedule, out var from, out var to))
        {
            _enabled = true;
            _from = from.ToString("HH:mm", CultureInfo.InvariantCulture);
            _to = to.ToString("HH:mm", CultureInfo.InvariantCulture);
        }
        else
        {
            _preserved = schedule;
            IsCustom = true;
        }
    }

    /// <summary>The schedule has a shape the form cannot show (set in the file): shown as a note, kept as it is.</summary>
    public bool IsCustom { get; }

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _from = "22:00";

    [ObservableProperty]
    private string _to = "07:00";

    [ObservableProperty]
    private bool _criticalPasses;

    /// <summary>Most messages per hour on this channel.</summary>
    [ObservableProperty]
    private string _limit;

    public int LimitValue => int.TryParse(Limit, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    /// <summary>The schedule to store; null means "no restriction".</summary>
    public ChannelSchedule? ToSchedule(out string? error)
    {
        error = null;
        if (IsCustom)
        {
            return _preserved;
        }

        if (!Enabled)
        {
            return CriticalPasses ? null : null;
        }

        if (!TimeOnly.TryParseExact(From, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mutedFrom)
            || !TimeOnly.TryParseExact(To, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var mutedTo)
            || mutedFrom == mutedTo)
        {
            error = "Orari non validi: usare il formato 22:00 e due orari diversi.";
            return null;
        }

        var windows = mutedFrom > mutedTo
            ? [new ChannelWindow { From = mutedTo, To = mutedFrom }]
            : new List<ChannelWindow>
            {
                new() { From = TimeOnly.MinValue, To = mutedFrom },
                new() { From = mutedTo, To = TimeOnly.MaxValue },
            };

        return new ChannelSchedule { Windows = windows, AlwaysAllowFrom = CriticalPasses ? Severity.Critical : null };
    }

    private static bool TryReadQuiet(ChannelSchedule schedule, out TimeOnly mutedFrom, out TimeOnly mutedTo)
    {
        mutedFrom = mutedTo = default;
        var windows = schedule.Windows;
        if (windows.Any(w => w.Days.Count > 0))
        {
            return false;
        }

        if (windows.Count == 1 && windows[0].From < windows[0].To)
        {
            // Allowed from A to B: quiet from B to A the next morning.
            mutedFrom = windows[0].To;
            mutedTo = windows[0].From;
            return mutedFrom != mutedTo && !(windows[0].From == TimeOnly.MinValue && windows[0].To == TimeOnly.MaxValue);
        }

        if (windows.Count == 2 && windows[0].From == TimeOnly.MinValue && windows[1].To == TimeOnly.MaxValue && windows[0].To < windows[1].From)
        {
            mutedFrom = windows[0].To;
            mutedTo = windows[1].From;
            return true;
        }

        return false;
    }
}

/// <summary>Shared by the four channel forms: a name, quiet hours and a test button.</summary>
public abstract partial class ChannelEditor(string id, string title, QuietHoursEditor quiet, Func<string, Task<ChannelTestResult>> test)
    : ObservableObject
{
    public string Id { get; } = id;

    public string Title { get; } = title;

    public QuietHoursEditor Quiet { get; } = quiet;

    [ObservableProperty]
    private bool _enabled;

    [ObservableProperty]
    private string _testText = "";

    /// <summary>Set by the parent: testing only makes sense for what is saved, not for unsaved edits.</summary>
    public Func<bool> HasUnsavedChanges { get; set; } = () => false;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (HasUnsavedChanges())
        {
            TestText = "Salva prima di provare: la prova usa le impostazioni salvate.";
            return;
        }

        TestText = "Invio di prova…";
        var result = await test(Id);
        TestText = result.Ok ? "Messaggio di prova inviato." : $"Non riuscito: {result.Error}";
    }

    protected static string Lines(IEnumerable<string> items) => string.Join(Environment.NewLine, items);

    protected static List<string> Split(string text) =>
        [.. text.Split(['\n', '\r', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>
/// The Outlook account the e-mail channel sends as. The sign-in is done by the service (the app only shows the code
/// to type), so the app never sees a password or a token.
/// </summary>
public sealed partial class OutlookSession(
    Func<ILiveHelpDeskApi?> api, Func<string, Task> copyToClipboard, Action<Uri> openInBrowser, Func<bool> hasUnsavedChanges) : ObservableObject
{
    private Uri? _loginUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedIn), nameof(ShowSignIn))]
    private string _account = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSignIn))]
    private bool _loginPending;

    [ObservableProperty]
    private string _loginCode = "";

    [ObservableProperty]
    private string _loginHelp = "";

    [ObservableProperty]
    private string _message = "";

    public bool IsSignedIn => !string.IsNullOrEmpty(Account);

    public bool ShowSignIn => !IsSignedIn && !LoginPending;

    /// <summary>Asks the service who is signed in to Outlook.</summary>
    public async Task RefreshAsync()
    {
        if (api() is not { } service)
        {
            return;
        }

        try
        {
            Apply(await service.GetMailAccountAsync());
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Message = $"Impossibile leggere l'accesso a Outlook: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (hasUnsavedChanges())
        {
            Message = "Salva prima: l'accesso usa le impostazioni salvate (tenant e applicazione).";
            return;
        }

        if (api() is not { } service)
        {
            Message = "Il servizio non è raggiungibile.";
            return;
        }

        try
        {
            var challenge = await service.StartMailLoginAsync();
            _loginUrl = Uri.TryCreate(challenge.VerificationUrl, UriKind.Absolute, out var url) ? url : null;
            LoginCode = challenge.UserCode;
            LoginHelp = $"Apri {challenge.VerificationUrl}, inserisci il codice e accedi con l'account Outlook aziendale. Il codice scade alle {challenge.ExpiresAt.ToLocalTime():HH:mm}.";
            Message = "";
            LoginPending = true;
            if (_loginUrl is not null)
            {
                openInBrowser(_loginUrl);
            }

            await WaitForSignInAsync(challenge.ExpiresAt, service);
        }
        catch (Exception ex) when (IsRemote(ex) || ex is TimeoutException)
        {
            LoginPending = false;
            Message = $"Impossibile avviare l'accesso a Outlook: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        if (api() is not { } service)
        {
            return;
        }

        try
        {
            await service.SignOutMailAsync();
            Account = "";
            Message = "Accesso a Outlook rimosso. L'e-mail aspetta in coda finché non si accede di nuovo.";
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Message = $"Non riuscito: {ex.Message}";
        }
    }

    [RelayCommand]
    private Task CopyCodeAsync() => string.IsNullOrEmpty(LoginCode) ? Task.CompletedTask : copyToClipboard(LoginCode);

    [RelayCommand]
    private void OpenPage()
    {
        if (_loginUrl is not null)
        {
            openInBrowser(_loginUrl);
        }
    }

    private async Task WaitForSignInAsync(DateTimeOffset expires, ILiveHelpDeskApi service)
    {
        while (true)
        {
            var info = await service.GetMailAccountAsync();
            if (!info.LoginPending || DateTimeOffset.UtcNow > expires)
            {
                Apply(info);
                Message = info.Account is null
                    ? $"Accesso a Outlook non completato. {info.LastError}".Trim()
                    : $"Collegato come {info.Account}.";
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private void Apply(MailAccountInfo info)
    {
        Account = info.Account ?? "";
        LoginPending = info.LoginPending;
        if (!LoginPending)
        {
            LoginCode = "";
            LoginHelp = "";
        }
    }

    private static bool IsRemote(Exception ex) =>
        ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException;
}

public sealed partial class EmailEditor : ChannelEditor
{
    public EmailEditor(EmailSettings s, QuietHoursEditor quiet, Func<string, Task<ChannelTestResult>> test, OutlookSession? session = null)
        : base("email", "E-mail", quiet, test)
    {
        Session = session ?? new OutlookSession(() => null, _ => Task.CompletedTask, _ => { }, () => false);
        Enabled = s.Enabled;
        _provider = Providers.First(c => c.Value == s.Provider.ToString());
        _tenantId = s.Outlook.TenantId;
        _clientId = s.Outlook.ClientId;
        _saveToSentItems = s.Outlook.SaveToSentItems;
        _host = s.Host;
        _port = s.Port.ToString(CultureInfo.InvariantCulture);
        _security = Securities.First(c => c.Value == s.Security.ToString());
        _user = s.User ?? "";
        _password = s.Password ?? "";
        _from = s.From;
        _to = Lines(s.To);
    }

    public static IReadOnlyList<Choice> Providers { get; } =
    [
        new(nameof(EmailProvider.Outlook), "Outlook / Microsoft 365 (accesso con il tuo account, senza password)"),
        new(nameof(EmailProvider.Smtp), "Server SMTP (utente e password)"),
    ];

    /// <summary>The Outlook sign-in; its changes are not edits of the settings, so they never mark the form as modified.</summary>
    public OutlookSession Session { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOutlook), nameof(IsSmtp))]
    private Choice _provider;

    public bool IsOutlook => Provider.Value == nameof(EmailProvider.Outlook);

    public bool IsSmtp => !IsOutlook;

    [ObservableProperty]
    private string _tenantId;

    [ObservableProperty]
    private string _clientId;

    [ObservableProperty]
    private bool _saveToSentItems;

    public static IReadOnlyList<Choice> Securities { get; } =
    [
        new(nameof(EmailSecurity.StartTls), "STARTTLS (porta 587)"),
        new(nameof(EmailSecurity.SslOnConnect), "SSL/TLS (porta 465)"),
        new(nameof(EmailSecurity.None), "Nessuna (solo reti fidate)"),
    ];

    [ObservableProperty]
    private string _host;

    [ObservableProperty]
    private string _port;

    [ObservableProperty]
    private Choice _security;

    [ObservableProperty]
    private string _user;

    [ObservableProperty]
    private string _password;

    [ObservableProperty]
    private string _from;

    [ObservableProperty]
    private string _to;

    public void ApplyTo(EmailSettings s)
    {
        s.Enabled = Enabled;
        s.Provider = Enum.Parse<EmailProvider>(Provider.Value);
        s.Outlook.TenantId = TenantId.Trim();
        s.Outlook.ClientId = ClientId.Trim();
        s.Outlook.SaveToSentItems = SaveToSentItems;
        s.Host = Host.Trim();
        s.Port = int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 0;
        s.Security = Enum.Parse<EmailSecurity>(Security.Value);
        s.User = string.IsNullOrWhiteSpace(User) ? null : User.Trim();
        s.Password = string.IsNullOrEmpty(Password) ? null : Password;
        s.From = From.Trim();
        s.To = Split(To);
    }
}

public sealed partial class WebhookEditor : ChannelEditor
{
    public WebhookEditor(WebhookSettings s, QuietHoursEditor quiet, Func<string, Task<ChannelTestResult>> test)
        : base("teams", "Teams", quiet, test)
    {
        Enabled = s.Enabled;
        _url = s.Url;
        _format = Formats.First(c => c.Value == s.Format.ToString());
    }

    public static IReadOnlyList<Choice> Formats { get; } =
    [
        new(nameof(WebhookFormat.TeamsCard), "Scheda di Teams (webhook di Workflows)"),
        new(nameof(WebhookFormat.Json), "JSON generico (qualunque ricevitore)"),
    ];

    [ObservableProperty]
    private string _url;

    [ObservableProperty]
    private Choice _format;

    public void ApplyTo(WebhookSettings s)
    {
        s.Enabled = Enabled;
        s.Url = Url.Trim();
        s.Format = Enum.Parse<WebhookFormat>(Format.Value);
    }
}

public sealed partial class TwilioEditor : ChannelEditor
{
    public TwilioEditor(string id, string title, bool whatsApp, TwilioSettings s, QuietHoursEditor quiet, Func<string, Task<ChannelTestResult>> test)
        : base(id, title, quiet, test)
    {
        ShowTemplate = whatsApp;
        Enabled = s.Enabled;
        _accountSid = s.AccountSid;
        _authToken = s.AuthToken;
        _from = s.From;
        _to = Lines(s.To);
        _contentSid = s.ContentSid ?? "";
    }

    /// <summary>WhatsApp conversations started by a company need an approved template.</summary>
    public bool ShowTemplate { get; }

    [ObservableProperty]
    private string _accountSid;

    [ObservableProperty]
    private string _authToken;

    [ObservableProperty]
    private string _from;

    [ObservableProperty]
    private string _to;

    [ObservableProperty]
    private string _contentSid;

    public void ApplyTo(TwilioSettings s)
    {
        s.Enabled = Enabled;
        s.AccountSid = AccountSid.Trim();
        s.AuthToken = AuthToken;
        s.From = From.Trim();
        s.To = Split(To);
        s.ContentSid = string.IsNullOrWhiteSpace(ContentSid) ? null : ContentSid.Trim();
    }
}

/// <summary>The outside channels' settings. Secrets come back masked; leaving the mask in place keeps the stored secret.</summary>
public sealed partial class ChannelsViewModel(
    Func<ILiveHelpDeskApi?> api, Func<string, Task>? copyToClipboard = null, Action<Uri>? openInBrowser = null) : ObservableObject
{
    private ChannelSettings _loaded = new();

    public EmailEditor? Email { get; private set; }

    public WebhookEditor? Teams { get; private set; }

    public TwilioEditor? Sms { get; private set; }

    public TwilioEditor? WhatsApp { get; private set; }

    public IReadOnlyList<ChannelEditor> All => [.. new ChannelEditor?[] { Email, Teams, Sms, WhatsApp }.OfType<ChannelEditor>()];

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private bool _isDirty;

    [ObservableProperty]
    private bool _isLoaded;

    public async Task LoadAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile.";
            return;
        }

        try
        {
            Build(await service.GetChannelSettingsAsync());
            Status = "";
            await Email!.Session.RefreshAsync();
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = $"Impossibile leggere le impostazioni: {ex.Message}";
        }
    }

    private void Build(ChannelSettings settings)
    {
        _loaded = settings;
        Task<ChannelTestResult> Test(string id) => TestAsync(id);
        int Limit(string id) => settings.MaxPerHour.TryGetValue(id, out var limit) ? limit : 20;
        QuietHoursEditor Quiet(string id) => new(settings.Schedules.GetValueOrDefault(id), Limit(id));

        Email = new EmailEditor(settings.Email, Quiet("email"), Test, new OutlookSession(
            api, copyToClipboard ?? (_ => Task.CompletedTask), openInBrowser ?? (_ => { }), () => IsDirty));
        Teams = new WebhookEditor(settings.Teams, Quiet("teams"), Test);
        Sms = new TwilioEditor("sms", "SMS", whatsApp: false, settings.Sms, Quiet("sms"), Test);
        WhatsApp = new TwilioEditor("whatsapp", "WhatsApp", whatsApp: true, settings.WhatsApp, Quiet("whatsapp"), Test);

        foreach (var editor in All)
        {
            editor.HasUnsavedChanges = () => IsDirty;
            editor.PropertyChanged += (_, e) => MarkDirty(e.PropertyName);
            editor.Quiet.PropertyChanged += (_, _) => MarkDirty(null);
        }

        IsDirty = false;
        IsLoaded = true;
        OnPropertyChanged(nameof(Email));
        OnPropertyChanged(nameof(Teams));
        OnPropertyChanged(nameof(Sms));
        OnPropertyChanged(nameof(WhatsApp));
    }

    private void MarkDirty(string? property)
    {
        if (property is not (nameof(ChannelEditor.TestText) or "TestCommand"))
        {
            IsDirty = true;
        }
    }

    /// <summary>The settings as the form says now; errors for what cannot be turned into settings.</summary>
    public ChannelSettings? Compose(out List<string> errors)
    {
        errors = [];
        if (Email is null || Teams is null || Sms is null || WhatsApp is null)
        {
            return null;
        }

        var settings = new ChannelSettings
        {
            Schedules = new Dictionary<string, ChannelSchedule>(_loaded.Schedules, StringComparer.OrdinalIgnoreCase),
            MaxPerHour = new Dictionary<string, int>(_loaded.MaxPerHour, StringComparer.OrdinalIgnoreCase),
        };
        Email.ApplyTo(settings.Email);
        Teams.ApplyTo(settings.Teams);
        Sms.ApplyTo(settings.Sms);
        WhatsApp.ApplyTo(settings.WhatsApp);

        foreach (var editor in All)
        {
            var schedule = editor.Quiet.ToSchedule(out var error);
            if (error is not null)
            {
                errors.Add($"{editor.Title}: {error}");
            }
            else if (schedule is null)
            {
                settings.Schedules.Remove(editor.Id);
            }
            else
            {
                settings.Schedules[editor.Id] = schedule;
            }

            settings.MaxPerHour[editor.Id] = editor.Quiet.LimitValue;
        }

        return settings;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (api() is not { } service)
        {
            Status = "Il servizio non è raggiungibile: nulla è stato salvato.";
            return;
        }

        var settings = Compose(out var errors);
        if (settings is null || errors.Count > 0)
        {
            Status = errors.Count > 0 ? string.Join(" ", errors) : "Niente da salvare.";
            return;
        }

        try
        {
            await service.SaveChannelSettingsAsync(settings);
            Build(await service.GetChannelSettingsAsync());
            Status = $"Salvato alle {DateTime.Now:HH:mm:ss}. I segreti sono cifrati e non vengono più mostrati.";
            await Email!.Session.RefreshAsync();
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            Status = $"Non salvato: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task DiscardAsync() => await LoadAsync();

    private async Task<ChannelTestResult> TestAsync(string id)
    {
        if (api() is not { } service)
        {
            return new ChannelTestResult(false, "il servizio non è raggiungibile");
        }

        try
        {
            return await service.TestChannelAsync(id);
        }
        catch (Exception ex) when (IsRemote(ex))
        {
            return new ChannelTestResult(false, ex.Message);
        }
    }

    private static bool IsRemote(Exception ex) =>
        ex is StreamJsonRpc.RemoteInvocationException or StreamJsonRpc.ConnectionLostException or IOException;
}
