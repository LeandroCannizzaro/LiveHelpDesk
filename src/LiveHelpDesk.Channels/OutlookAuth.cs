using LiveHelpDesk.Abstractions;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace LiveHelpDesk.Channels;

public enum MailAuthFailure
{
    /// <summary>Nobody is signed in to Outlook, or the sign-in expired: the user has to sign in again.</summary>
    AuthRequired,

    /// <summary>The Microsoft sign-in service could not be reached: try again later.</summary>
    Unavailable,
}

public sealed class MailAuthException(MailAuthFailure failure, string message, Exception? inner = null) : Exception(message, inner)
{
    public MailAuthFailure Failure { get; } = failure;
}

/// <summary>What the user must do to sign in to Outlook: open the address and type the code.</summary>
public sealed record MailDeviceCode(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn, string Message);

/// <summary>The access tokens the Graph sender needs; faked in tests.</summary>
public interface IMailTokenSource
{
    /// <exception cref="MailAuthException">No usable sign-in, or Microsoft cannot be reached.</exception>
    Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken);

    /// <summary>The signed-in account (its address), if any.</summary>
    Task<string?> AccountNameAsync(CancellationToken cancellationToken);

    /// <summary>True once a call found nobody signed in: messages are then kept queued instead of failing one by one.</summary>
    bool KnownSignedOut { get; }
}

/// <summary>
/// Delegated sign-in to Microsoft 365 for sending mail as the user (permission <c>Mail.Send</c>). Device code, so
/// it works with passkeys / Windows Hello. The token cache is DPAPI-protected and kept in its own file, apart from
/// the Dynamics one: signing out of Outlook never touches the Dynamics sign-in.
/// </summary>
public sealed class OutlookAuth(string cacheDirectory, Func<OutlookSettings> settings, string defaultTenant) : IMailTokenSource
{
    /// <summary>Microsoft Graph Command Line Tools: a public client that organisations can allow for Mail.Send.</summary>
    public const string DefaultClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    public const string MailSendScope = "https://graph.microsoft.com/Mail.Send";

    private static readonly string[] Scopes = [MailSendScope];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IPublicClientApplication? _app;
    private string? _appKey;
    private volatile bool _signedOut;

    public bool KnownSignedOut => _signedOut;

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var app = await GetAppAsync();
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is null)
        {
            _signedOut = true;
            throw new MailAuthException(MailAuthFailure.AuthRequired, "Serve l'accesso a Outlook: accedere dalle impostazioni del canale e-mail.");
        }

        try
        {
            var result = await app.AcquireTokenSilent(Scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            _signedOut = false;
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            _signedOut = true;
            throw new MailAuthException(
                MailAuthFailure.AuthRequired, "L'accesso a Outlook è scaduto o non basta più: accedere di nuovo dalle impostazioni del canale e-mail.", ex);
        }
        catch (MsalServiceException ex) when (ex.StatusCode is 0 or >= 500)
        {
            throw new MailAuthException(MailAuthFailure.Unavailable, $"Servizio di accesso Microsoft non disponibile: {ex.Message}", ex);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.HttpStatusNotFound || ex.ErrorCode == "request_timeout")
        {
            throw new MailAuthException(MailAuthFailure.Unavailable, $"Accesso Microsoft non raggiungibile: {ex.Message}", ex);
        }
    }

    public async Task<string?> AccountNameAsync(CancellationToken cancellationToken = default)
    {
        var name = (await (await GetAppAsync()).GetAccountsAsync()).FirstOrDefault()?.Username;
        _signedOut = name is null;
        return name;
    }

    /// <summary>Calls <paramref name="show"/> with the code to type, then completes with the account's address.</summary>
    public async Task<string> LoginWithDeviceCodeAsync(Func<MailDeviceCode, Task> show, CancellationToken cancellationToken)
    {
        var app = await GetAppAsync();
        var result = await app
            .AcquireTokenWithDeviceCode(Scopes, code => show(new MailDeviceCode(code.UserCode, code.VerificationUrl, code.ExpiresOn, code.Message)))
            .ExecuteAsync(cancellationToken);
        _signedOut = false;
        return result.Account.Username;
    }

    public async Task SignOutAsync()
    {
        var app = await GetAppAsync();
        foreach (var account in await app.GetAccountsAsync())
        {
            await app.RemoveAsync(account);
        }

        _signedOut = true;
    }

    /// <summary>The application follows the tenant and client in the settings; it is rebuilt only when they change.</summary>
    private async Task<IPublicClientApplication> GetAppAsync()
    {
        var current = settings();
        var tenant = string.IsNullOrWhiteSpace(current.TenantId) ? defaultTenant : current.TenantId.Trim();
        var clientId = string.IsNullOrWhiteSpace(current.ClientId) ? DefaultClientId : current.ClientId.Trim();
        var key = $"{tenant}|{clientId}";

        await _gate.WaitAsync();
        try
        {
            if (_app is not null && _appKey == key)
            {
                return _app;
            }

            var app = PublicClientApplicationBuilder.Create(clientId)
                .WithAuthority($"https://login.microsoftonline.com/{tenant}")
                .WithRedirectUri("http://localhost")
                .Build();

            Directory.CreateDirectory(cacheDirectory);
            var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("msal-mail-cache.bin", cacheDirectory).Build());
            cache.RegisterCache(app.UserTokenCache);

            _app = app;
            _appKey = key;
            return app;
        }
        finally
        {
            _gate.Release();
        }
    }
}
