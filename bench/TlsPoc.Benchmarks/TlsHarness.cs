using System.Buffers;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TlsPoc.Core;

namespace TlsPoc.Benchmarks;

/// <summary>
/// Shared setup: certificates, authentication options and connected TLS endpoint pairs
/// for both the SslStream baseline and the TlsBufferSession PoC.
/// </summary>
public static class TlsHarness
{
    public const string HostName = "localhost";

    public static X509Certificate2 CreateCertificate() => CertificateFactory.CreateSelfSigned(HostName);

    public static SslServerAuthenticationOptions ServerOptions(X509Certificate2 certificate) => new()
    {
        ServerCertificate = certificate,
        EnabledSslProtocols = SslProtocols.Tls13,
        ApplicationProtocols = [SslApplicationProtocol.Http11],
        ClientCertificateRequired = false,
        // Disabled so every measured handshake is a full handshake rather than a resumption.
        AllowTlsResume = false,
    };

    public static SslClientAuthenticationOptions ClientOptions() => new()
    {
        TargetHost = HostName,
        EnabledSslProtocols = SslProtocols.Tls13,
        ApplicationProtocols = [SslApplicationProtocol.Http11],
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
        AllowTlsResume = false,
    };

    public static async Task<SslStreamPair> ConnectSslStreamAsync(
        SslServerAuthenticationOptions serverOptions,
        SslClientAuthenticationOptions clientOptions)
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new SslStreamDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.AuthenticateAsServerAsync(serverOptions),
            client.AuthenticateAsClientAsync(clientOptions));

        return new SslStreamPair(client, server);
    }

    public static async Task<TlsSessionPair> ConnectTlsSessionAsync(
        TlsContext serverContext,
        TlsContext clientContext)
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new TlsSessionDuplexPipe(serverTransport);
        var client = new TlsSessionDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.HandshakeAsync(serverContext),
            client.HandshakeAsync(clientContext));

        return new TlsSessionPair(client, server);
    }

    /// <summary>
    /// Minimal inline handshake driver: no wrapper objects, pooled scratch buffers only.
    /// Used to attribute per-connection allocation between the session and the pipe adapter.
    /// </summary>
    public static async Task DriveServerHandshakeAsync(TlsBufferSession session, IDuplexPipe transport, bool useDefaultValidation = false)
    {
        const int MaxCipherRecord = 16 * 1024 + 512;

        var scratch = ArrayPool<byte>.Shared.Rent(MaxCipherRecord);
        var result = default(ReadResult);
        var buffer = ReadOnlySequence<byte>.Empty;
        var holdsResult = false;

        try
        {
            while (!session.IsHandshakeComplete)
            {
                ReadOnlySpan<byte> source = default;
                if (holdsResult && !buffer.IsEmpty)
                {
                    var slice = buffer.Length > MaxCipherRecord ? buffer.Slice(0, MaxCipherRecord) : buffer;
                    if (slice.IsSingleSegment)
                    {
                        source = slice.FirstSpan;
                    }
                    else
                    {
                        slice.CopyTo(scratch);
                        source = scratch.AsSpan(0, (int)slice.Length);
                    }
                }

                var destination = transport.Output.GetSpan(MaxCipherRecord);
                var status = session.Handshake(source, destination, out var consumed, out var written);
                transport.Output.Advance(written);

                if (holdsResult && consumed > 0)
                {
                    buffer = buffer.Slice(consumed);
                }

                if (written > 0)
                {
                    await transport.Output.FlushAsync();
                }

                switch (status)
                {
                    case TlsOperationStatus.Complete:
                    case TlsOperationStatus.DestinationTooSmall:
                        continue;

                    case TlsOperationStatus.NeedsCertificateValidation:
                        if (useDefaultValidation)
                        {
                            session.AcceptWithDefaultValidation();
                        }
                        else
                        {
                            session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
                        }
                        continue;

                    case TlsOperationStatus.NeedMoreData:
                        if (holdsResult)
                        {
                            transport.Input.AdvanceTo(buffer.Start, result.Buffer.End);
                            holdsResult = false;
                        }

                        result = await transport.Input.ReadAsync();
                        buffer = result.Buffer;
                        holdsResult = true;
                        continue;

                    default:
                        throw new InvalidOperationException($"Unexpected handshake status {status}.");
                }
            }
        }
        finally
        {
            if (holdsResult)
            {
                transport.Input.AdvanceTo(buffer.Start, buffer.Start);
            }

            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public static async ValueTask RoundTripAsync(IDuplexPipe client, IDuplexPipe server, byte[] payload)    {
        client.Output.Write(payload);
        var clientFlush = client.Output.FlushAsync();

        await ReadExactlyAsync(server.Input, payload.Length);
        await clientFlush;

        server.Output.Write(payload);
        var serverFlush = server.Output.FlushAsync();

        await ReadExactlyAsync(client.Input, payload.Length);
        await serverFlush;
    }

    public static async ValueTask ReadExactlyAsync(PipeReader reader, int count)
    {
        var remaining = count;

        while (remaining > 0)
        {
            var result = await reader.ReadAsync();
            var buffer = result.Buffer;

            if (buffer.IsEmpty && result.IsCompleted)
            {
                throw new InvalidOperationException($"Peer completed with {remaining} bytes outstanding.");
            }

            var take = (int)Math.Min(buffer.Length, remaining);
            reader.AdvanceTo(buffer.GetPosition(take), buffer.End);
            remaining -= take;
        }
    }
}

public sealed class SslStreamPair(SslStreamDuplexPipe client, SslStreamDuplexPipe server) : IAsyncDisposable
{
    public SslStreamDuplexPipe Client { get; } = client;

    public SslStreamDuplexPipe Server { get; } = server;

    public ValueTask DisposeAsync() =>
        new(Task.WhenAll(Client.DisposeAsync().AsTask(), Server.DisposeAsync().AsTask()));
}

public sealed class TlsSessionPair(TlsSessionDuplexPipe client, TlsSessionDuplexPipe server) : IAsyncDisposable
{
    public TlsSessionDuplexPipe Client { get; } = client;

    public TlsSessionDuplexPipe Server { get; } = server;

    public ValueTask DisposeAsync() =>
        new(Task.WhenAll(Client.DisposeAsync().AsTask(), Server.DisposeAsync().AsTask()));
}
