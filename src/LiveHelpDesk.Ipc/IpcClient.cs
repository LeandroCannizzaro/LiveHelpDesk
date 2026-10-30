using System.IO.Pipes;
using StreamJsonRpc;

namespace LiveHelpDesk.Ipc;

/// <summary>A live connection to the service. Dispose to hang up; <see cref="Disconnected"/> ends when the service goes away.</summary>
public sealed class IpcConnection : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly NamedPipeClientStream _pipe;

    internal IpcConnection(JsonRpc rpc, NamedPipeClientStream pipe, ILiveHelpDeskApi api)
    {
        _rpc = rpc;
        _pipe = pipe;
        Api = api;
    }

    public ILiveHelpDeskApi Api { get; }

    public Task Disconnected => _rpc.Completion;

    public async ValueTask DisposeAsync()
    {
        _rpc.Dispose();
        await _pipe.DisposeAsync();
    }
}

public static class IpcClient
{
    /// <exception cref="TimeoutException">The service is not listening (stopped, not installed, or still starting).</exception>
    public static async Task<IpcConnection> ConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)timeout.TotalMilliseconds, cancellationToken);
        }
        catch
        {
            await pipe.DisposeAsync();
            throw;
        }

        // The service identifies us from this first byte (see PipeServer); it also tells it which protocol we speak.
        await pipe.WriteAsync(new[] { (byte)IpcProtocol.Version }, cancellationToken);
        await pipe.FlushAsync(cancellationToken);

        var rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new SystemTextJsonFormatter()));
        var api = rpc.Attach<ILiveHelpDeskApi>();
        rpc.StartListening();
        return new IpcConnection(rpc, pipe, api);
    }

    /// <summary>Like <see cref="ConnectAsync"/> but answers null instead of throwing when the service is not there.</summary>
    public static async Task<IpcConnection?> TryConnectAsync(string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            return await ConnectAsync(pipeName, timeout, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            return null;
        }
    }
}
