using System.IO.Pipelines;

namespace TlsPoc.Core;

/// <summary>
/// An in-memory pair of connected <see cref="IDuplexPipe"/> endpoints, mirroring
/// Kestrel's test transport. Used to isolate TLS cost from socket cost.
/// </summary>
public static class DuplexPipePair
{
    public static (IDuplexPipe Client, IDuplexPipe Server) Create(PipeOptions? options = null)
    {
        options ??= new PipeOptions(
            pauseWriterThreshold: 0,
            resumeWriterThreshold: 0,
            useSynchronizationContext: false);

        var clientToServer = new Pipe(options);
        var serverToClient = new Pipe(options);

        var client = new Endpoint(serverToClient.Reader, clientToServer.Writer);
        var server = new Endpoint(clientToServer.Reader, serverToClient.Writer);
        return (client, server);
    }

    private sealed class Endpoint(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;

        public PipeWriter Output { get; } = output;
    }
}
