using LiveHelpDesk.Core.Sources;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace LiveHelpDesk.Dynamics;

/// <summary>
/// Delegated sign-in with the user's own account (no tenant admin involved) and a DPAPI-protected token
/// cache. The client id is the public one used by Microsoft's Power Platform CLI, which the tenant accepts
/// for Dataverse; an own app registration can replace it through <paramref name="clientId"/>.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    public const string PowerPlatformCliClientId = "9cee029c-6210-4654-90bb-17e6e9d36617";

    private readonly IPublicClientApplication _app;
    private readonly string[] _scopes;

    private MsalTokenProvider(IPublicClientApplication app, string scope)
    {
        _app = app;
        _scopes = [scope];
    }

    public static async Task<MsalTokenProvider> CreateAsync(
        DynamicsOptions options, string cacheDirectory, string? clientId = null)
    {
        var app = PublicClientApplicationBuilder.Create(clientId ?? PowerPlatformCliClientId)
            .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}")
            .WithRedirectUri("http://localhost")
            .Build();

        Directory.CreateDirectory(cacheDirectory);
        var cache = await MsalCacheHelper.CreateAsync(new StorageCreationPropertiesBuilder("msal-cache.bin", cacheDirectory).Build());
        cache.RegisterCache(app.UserTokenCache);
        return new MsalTokenProvider(app, options.Scope);
    }

    public async Task<bool> HasAccountAsync() => (await _app.GetAccountsAsync()).Any();

    public async Task<string> GetTokenAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        var account = (await _app.GetAccountsAsync()).FirstOrDefault()
            ?? throw new TicketSourceException(SourceFailure.AuthRequired, "Nessun accesso a Dynamics: eseguire il login.");

        try
        {
            var result = await _app.AcquireTokenSilent(_scopes, account)
                .WithForceRefresh(forceRefresh)
                .ExecuteAsync(cancellationToken);
            return result.AccessToken;
        }
        catch (MsalUiRequiredException ex)
        {
            throw new TicketSourceException(
                SourceFailure.AuthRequired, "L'accesso a Dynamics è scaduto: eseguire di nuovo il login.", inner: ex);
        }
        catch (MsalServiceException ex) when (ex.StatusCode is 0 or >= 500)
        {
            throw new TicketSourceException(SourceFailure.Unavailable, $"Servizio di accesso Microsoft non disponibile: {ex.Message}", inner: ex);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == MsalError.HttpStatusNotFound || ex.ErrorCode == "request_timeout")
        {
            throw new TicketSourceException(SourceFailure.Unavailable, $"Accesso Microsoft non raggiungibile: {ex.Message}", inner: ex);
        }
    }

    public async Task<string?> AccountNameAsync() => (await _app.GetAccountsAsync()).FirstOrDefault()?.Username;

    /// <summary>Device code sign-in: the user opens a page and types a short code (works with passkeys / Windows Hello).</summary>
    public async Task<string> LoginWithDeviceCodeAsync(Func<DeviceCodeInfo, Task> show, CancellationToken cancellationToken)
    {
        var result = await _app
            .AcquireTokenWithDeviceCode(_scopes, code => show(new DeviceCodeInfo(code.UserCode, code.VerificationUrl, code.ExpiresOn, code.Message)))
            .ExecuteAsync(cancellationToken);
        return result.Account.Username;
    }
}

public sealed record DeviceCodeInfo(string UserCode, string VerificationUrl, DateTimeOffset ExpiresOn, string Message);
