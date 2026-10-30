using LiveHelpDesk.Dynamics;
using LiveHelpDesk.Ipc;
using Microsoft.Extensions.Logging;

namespace LiveHelpDesk.Service;

/// <summary>
/// One device-code sign-in at a time for a profile. The service runs it, so the token never passes through the
/// app: the app only shows the code to the user.
/// </summary>
/// <param name="login">Starts the sign-in; calls the callback with the code, completes with the user name.</param>
/// <param name="onSuccess">Runs after a successful sign-in (look up the user, wake the poller...).</param>
public sealed class LoginCoordinator(
    Func<Func<DeviceCodeInfo, Task>, CancellationToken, Task<string>> login,
    Func<string, Task> onSuccess,
    ILogger logger,
    CancellationToken stopping)
{
    private readonly object _gate = new();
    private Task? _running;
    private Task<LoginChallenge>? _challenge;

    public bool InProgress
    {
        get
        {
            lock (_gate)
            {
                return _running is { IsCompleted: false };
            }
        }
    }

    public string? LastError { get; private set; }

    /// <summary>Asking again while a sign-in is waiting returns the same code instead of starting another.</summary>
    public Task<LoginChallenge> StartAsync()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } && _challenge is not null)
            {
                return _challenge;
            }

            var code = new TaskCompletionSource<LoginChallenge>(TaskCreationOptions.RunContinuationsAsynchronously);
            LastError = null;
            _challenge = code.Task;
            _running = Task.Run(async () =>
            {
                try
                {
                    var user = await login(
                        info =>
                        {
                            code.TrySetResult(new LoginChallenge(info.UserCode, info.VerificationUrl, info.ExpiresOn, info.Message));
                            return Task.CompletedTask;
                        },
                        stopping);
                    logger.LogInformation("Signed in as {User}", user);
                    await onSuccess(user);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    logger.LogWarning("Sign-in did not complete: {Message}", ex.Message);
                    code.TrySetException(ex);
                }
            });
            return _challenge;
        }
    }
}
