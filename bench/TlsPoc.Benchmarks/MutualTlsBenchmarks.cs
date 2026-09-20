using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;
using TlsPoc.Core;

namespace TlsPoc.Benchmarks;

/// <summary>
/// Mutual TLS: the client presents a certificate, so the server genuinely has a chain to
/// build. Tests whether it costs more for the caller (Kestrel) to drive validation via
/// AcceptWithDefaultValidation than it does for SslStream to do it internally.
///
/// The "skip validation" row is not a legitimate production configuration - it exists only
/// to bound how much of each row is the chain build itself.
/// </summary>
[MemoryDiagnoser]
public class MutualTlsBenchmarks
{
    private X509Certificate2 _serverCertificate = null!;
    private X509Certificate2 _clientCertificate = null!;
    private SslServerAuthenticationOptions _serverOptions = null!;
    private SslClientAuthenticationOptions _clientOptions = null!;
    private TlsContext _serverContext = null!;

    [GlobalSetup]
    public void Setup()
    {
        _serverCertificate = CertificateFactory.CreateSelfSigned("localhost");
        _clientCertificate = CertificateFactory.CreateSelfSigned("client", clientAuth: true);

        _serverOptions = ServerOptions();
        _clientOptions = new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls13,
            ClientCertificates = [_clientCertificate],
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            AllowTlsResume = false,
        };

        _serverContext = TlsContext.CreateServer(ServerOptions());
    }

    private SslServerAuthenticationOptions ServerOptions() => new()
    {
        ServerCertificate = _serverCertificate,
        EnabledSslProtocols = SslProtocols.Tls13,
        ClientCertificateRequired = true,
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
        AllowTlsResume = false,
    };

    [GlobalCleanup]
    public void Cleanup()
    {
        _serverContext.Dispose();
        _serverCertificate.Dispose();
        _clientCertificate.Dispose();
    }

    [Benchmark(Baseline = true, Description = "SslStream server validates internally")]
    public async Task SslStreamServer()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new SslStreamDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.AuthenticateAsServerAsync(_serverOptions),
            client.AuthenticateAsClientAsync(_clientOptions));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    [Benchmark(Description = "PoC server, AcceptWithDefaultValidation")]
    public async Task PocServerDefaultValidation()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new TlsSessionDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.HandshakeAsync(_serverContext, onCertificateValidation: s => s.AcceptWithDefaultValidation()),
            client.AuthenticateAsClientAsync(_clientOptions));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    [Benchmark(Description = "PoC server, skip validation (cost floor only)")]
    public async Task PocServerSkipValidation()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new TlsSessionDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.HandshakeAsync(_serverContext),
            client.AuthenticateAsClientAsync(_clientOptions));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }
}
