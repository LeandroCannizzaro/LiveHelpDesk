using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace LiveHelpDesk.Ipc;

/// <summary>
/// The service's end of the conversation. Anyone logged on to this machine may connect; what they get is the
/// profile of the Windows identity that connected, never another one.
/// </summary>
/// <param name="resolveProfile">Maps the caller's Windows SID to that user's API.</param>
public sealed class PipeServer(
    string pipeName,
    Func<string, CancellationToken, Task<ILiveHelpDeskApi>> resolveProfile,
    ILogger logger)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var first = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            var pipe = CreatePipe(pipeName, first);
            first = false;
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                break;
            }

            _ = Task.Run(() => ServeAsync(pipe, cancellationToken), CancellationToken.None);
        }
    }

    /// <summary>
    /// Everyone signed in may talk to the service; nobody over the network. The first instance is created
    /// exclusively, so nothing else can already be squatting on the name when we start.
    /// </summary>
    public static NamedPipeServerStream CreatePipe(string pipeName, bool exclusive)
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));

        var options = PipeOptions.Asynchronous | (exclusive ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(
            pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                // Windows only lets a server learn who is on the other end after it has read something from them:
                // the client opens with one byte, its protocol version.
                var hello = new byte[1];
                await pipe.ReadExactlyAsync(hello, cancellationToken);
                if (hello[0] != IpcProtocol.Version)
                {
                    logger.LogWarning("Pipe client speaks protocol {Version}, this service speaks {Ours}: closing", hello[0], IpcProtocol.Version);
                    return;
                }

                string? sid = null;
                pipe.RunAsClient(() => sid = WindowsIdentity.GetCurrent().User?.Value);
                if (sid is null)
                {
                    logger.LogWarning("Pipe client without a Windows identity: closing");
                    return;
                }

                var api = await resolveProfile(sid, cancellationToken);
                using var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
                rpc.AddLocalRpcTarget<ILiveHelpDeskApi>(api, new JsonRpcTargetOptions { NotifyClientOfEvents = true });
                rpc.StartListening();
                await rpc.Completion.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex) when (ex is IOException or ConnectionLostException or ObjectDisposedException)
            {
                logger.LogDebug("Pipe client disconnected: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Pipe connection failed");
            }
        }
    }
}
