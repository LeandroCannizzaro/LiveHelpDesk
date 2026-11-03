using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LiveHelpDesk.Abstractions;
using LiveHelpDesk.Channels;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>
/// Passwords and tokens are stored encrypted with the account the service runs as (DPAPI): other users of the
/// machine, and backups of the folder taken elsewhere, cannot read them.
/// </summary>
public static class SecretProtector
{
    public const string Prefix = "enc:";

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LiveHelpDesk.channels.v1");

    public static bool IsProtected(string? text) => text?.StartsWith(Prefix, StringComparison.Ordinal) == true;

    public static string Protect(string plain) =>
        Prefix + Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser));

    /// <summary>Plain text passes through unchanged, so a hand-edited file with a new secret still works.</summary>
    public static string Reveal(string stored) =>
        IsProtected(stored)
            ? Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored[Prefix.Length..]), Entropy, DataProtectionScope.CurrentUser))
            : stored;
}

/// <summary>
/// The user's channel settings in <c>channels.json</c>. Secrets are encrypted on disk and in the file only; the
/// app never receives them back (it sees a mask), and sending the mask back keeps the stored secret.
/// </summary>
public sealed class ChannelStore
{
    public const string Mask = "********";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly (Func<ChannelSettings, string?> Get, Action<ChannelSettings, string?> Set)[] Secrets =
    [
        (s => s.Email.Password, (s, v) => s.Email.Password = v),
        (s => s.Teams.Url, (s, v) => s.Teams.Url = v ?? ""),
        (s => s.Sms.AuthToken, (s, v) => s.Sms.AuthToken = v ?? ""),
        (s => s.WhatsApp.AuthToken, (s, v) => s.WhatsApp.AuthToken = v ?? ""),
    ];

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    private ChannelSettings _current = new();
    private DateTime _loadedFrom = DateTime.MinValue;

    public ChannelStore(string path, ILogger logger)
    {
        _path = path;
        _logger = logger;
        Reload();
    }

    /// <summary>Changes whenever the settings do; lets callers rebuild what depends on them.</summary>
    public long Version { get; private set; }

    /// <summary>The settings with secrets in the clear (service use only).</summary>
    public ChannelSettings Current
    {
        get
        {
            lock (_gate)
            {
                if (File.GetLastWriteTimeUtc(_path) != _loadedFrom)
                {
                    Reload();
                }

                return _current;
            }
        }
    }

    /// <summary>What the app is allowed to see: the same settings with every secret masked.</summary>
    public static ChannelSettings Masked(ChannelSettings settings)
    {
        var copy = Clone(settings);
        foreach (var (get, set) in Secrets)
        {
            if (!string.IsNullOrEmpty(get(copy)))
            {
                set(copy, Mask);
            }
        }

        return copy;
    }

    /// <exception cref="ArgumentException">The settings are not valid; the message lists what to fix.</exception>
    public void Save(ChannelSettings incoming)
    {
        lock (_gate)
        {
            var merged = Clone(incoming);
            var existing = Current;
            foreach (var (get, set) in Secrets)
            {
                if (get(merged) == Mask)
                {
                    set(merged, get(existing)); // unchanged: keep what is stored
                }
            }

            var errors = ChannelSettingsValidator.Validate(merged);
            if (errors.Count > 0)
            {
                throw new ArgumentException(string.Join(" ", errors));
            }

            Write(merged);
            _current = merged;
            _loadedFrom = File.GetLastWriteTimeUtc(_path);
            Version++;
        }
    }

    private void Reload()
    {
        _loadedFrom = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : DateTime.MinValue;
        Version++;

        if (!File.Exists(_path))
        {
            _current = new ChannelSettings();
            return;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<ChannelSettings>(File.ReadAllText(_path), Json) ?? new ChannelSettings();
            var hadPlainSecret = false;
            foreach (var (get, set) in Secrets)
            {
                var value = get(stored);
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                hadPlainSecret |= !SecretProtector.IsProtected(value);
                set(stored, SecretProtector.Reveal(value));
            }

            _current = stored;
            if (hadPlainSecret)
            {
                // A secret typed into the file by hand: lock it away now.
                Write(stored);
                _loadedFrom = File.GetLastWriteTimeUtc(_path);
                _logger.LogInformation("Encrypted the secrets found in {Path}", _path);
            }
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            // A damaged file must not silence the monitor's other channels, nor be silently overwritten.
            _logger.LogError("Channel settings in {Path} could not be read, no outside channel is active: {Message}", _path, ex.Message);
            _current = new ChannelSettings();
        }
    }

    private void Write(ChannelSettings settings)
    {
        var protectedCopy = Clone(settings);
        foreach (var (get, set) in Secrets)
        {
            if (!string.IsNullOrEmpty(get(protectedCopy)))
            {
                set(protectedCopy, SecretProtector.Protect(get(protectedCopy)!));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(protectedCopy, Json));
        File.Move(temp, _path, overwrite: true);
    }

    private static ChannelSettings Clone(ChannelSettings settings) =>
        JsonSerializer.Deserialize<ChannelSettings>(JsonSerializer.Serialize(settings, Json), Json)!;
}

/// <summary>The live set of channels, rebuilt whenever the settings change.</summary>
public sealed class ChannelRegistry(ChannelStore store, HttpClient http, IEmailSender? emailSender = null, IMailTokenSource? outlookTokens = null)
{
    private readonly object _gate = new();
    private long _builtFor = -1;
    private IReadOnlyDictionary<string, INotificationChannel> _channels = new Dictionary<string, INotificationChannel>();

    public ChannelSettings Settings => store.Current;

    public INotificationChannel? Get(string id)
    {
        lock (_gate)
        {
            var settings = store.Current; // may bump the version
            if (_builtFor != store.Version)
            {
                _channels = ChannelFactory.Create(settings, http, emailSender, outlookTokens: outlookTokens);
                _builtFor = store.Version;
            }

            return _channels.GetValueOrDefault(id);
        }
    }

    /// <summary>
    /// Like <see cref="Get"/>, but e-mail through Outlook counts as absent while nobody is signed in: the queue keeps
    /// the messages until the user signs in, instead of burning their attempts and opening the circuit breaker.
    /// </summary>
    public INotificationChannel? GetForDelivery(string id) =>
        string.Equals(id, ChannelIds.Email, StringComparison.OrdinalIgnoreCase) && IsHeldForSignIn() ? null : Get(id);

    /// <summary>The e-mail channel is on, sends through Outlook, and nobody is signed in.</summary>
    public bool IsHeldForSignIn() =>
        store.Current.Email is { Enabled: true, Provider: EmailProvider.Outlook } && outlookTokens?.KnownSignedOut != false;
}
