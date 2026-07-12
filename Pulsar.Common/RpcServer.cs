using System.IO.Pipes;
using PolyType.ReflectionProvider;
using Serilog;
using StreamJsonRpc;

namespace Pulsar.Common;

/// <summary>
/// Simple JSON-RPC server against a single target, using Nerdbank MessagePack for the message codec.
/// Used by both the transcode and audio host.
/// </summary>
public static class RpcServer
{
    public static async Task NamedPipeServerAsync<T>(string serverName, CancellationToken token) where T : new()
    {
        var clientId = 0;
        while (!token.IsCancellationRequested)
        {
            Log.Information("Waiting for client connection...");
            var stream = new NamedPipeServerStream(
                serverName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            try
            {
                await stream.WaitForConnectionAsync(token);
            }
            catch
            {
                await stream.DisposeAsync();
                throw;
            }
            _ = RespondToRpcRequestAsync<T>(stream, ++clientId);
        }
    }

    public static JsonRpc BuildSharedJsonRpc(Stream stream)
    {
        var formatter = new NerdbankMessagePackFormatter()
        {
            TypeShapeProvider = ReflectionTypeShapeProvider.Default
        };
        var handler = new LengthHeaderMessageHandler(stream, stream, formatter);
        return new JsonRpc(handler);
    }

    private static async Task RespondToRpcRequestAsync<T>(Stream stream, int clientId) where T : new()
    {
        Log.Information("Client {id} connected", clientId);
        T? target = default;
        try
        {
            var jsonRpc = BuildSharedJsonRpc(stream);
            try
            {
                target = new T();
                jsonRpc.AddLocalRpcTarget(target);
                jsonRpc.StartListening();

                Log.Information("JSON-RPC listener attached to client {id}. Awaiting requests...", clientId);
                await jsonRpc.Completion;
                Log.Information("Client {id} disconnected", clientId);
            }
            finally
            {
                jsonRpc.Dispose();
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Client {id} connection ended with error", clientId);
        }
        finally
        {
            switch (target)
            {
                case IAsyncDisposable ad:
                    await ad.DisposeAsync();
                    break;
                case IDisposable d:
                    d.Dispose();
                    break;
            }

            await stream.DisposeAsync();
        }
    }
}
