using System.IO.Pipelines;
using System.Net.Security;

namespace TlsPoc.Core;

/// <summary>
/// Baseline: exactly what Kestrel does today (<c>SslDuplexPipe</c> /
/// <c>DuplexPipeStreamAdapter&lt;SslStream&gt;</c>).
///
/// The data path is
/// transport pipe -> DuplexPipeStream -> SslStream -> StreamPipeReader/StreamPipeWriter -> app,
/// which costs two extra buffer copies and two extra async layers in each direction.
/// </summary>
public sealed class SslStreamDuplexPipe : IDuplexPipe, IAsyncDisposable
{
    private readonly IDuplexPipe _transport;
    private readonly DuplexPipeStream _stream;

    public SslStreamDuplexPipe(IDuplexPipe transport)
    {
        _transport = transport;
        _stream = new DuplexPipeStream(transport.Input, transport.Output);
        SslStream = new SslStream(_stream, leaveInnerStreamOpen: true);
        Input = PipeReader.Create(SslStream, new StreamPipeReaderOptions(leaveOpen: true));
        Output = PipeWriter.Create(SslStream, new StreamPipeWriterOptions(leaveOpen: true));
    }

    public SslStream SslStream { get; }

    public PipeReader Input { get; }

    public PipeWriter Output { get; }

    public Task AuthenticateAsServerAsync(SslServerAuthenticationOptions options, CancellationToken cancellationToken = default)
        => SslStream.AuthenticateAsServerAsync(options, cancellationToken);

    public Task AuthenticateAsClientAsync(SslClientAuthenticationOptions options, CancellationToken cancellationToken = default)
        => SslStream.AuthenticateAsClientAsync(options, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await SslStream.DisposeAsync();

        // Kestrel's DuplexPipeStreamAdapter completes both ends here; without it the
        // StreamPipeReader/Writer and the transport pipe never return their pooled
        // segments to the ArrayPool.
        await Input.CompleteAsync();
        await Output.CompleteAsync();

        await _transport.Output.CompleteAsync();
        await _transport.Input.CompleteAsync();

        await _stream.DisposeAsync();
    }
}
