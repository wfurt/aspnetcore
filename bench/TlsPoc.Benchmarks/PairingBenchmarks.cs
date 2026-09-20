using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;
using TlsPoc.Core;

namespace TlsPoc.Benchmarks;

/// <summary>
/// Full pairing matrix. A hand-rolled GC.GetTotalAllocatedBytes probe suggested that mixed
/// SslStream/PoC pairings cost ~5x a homogeneous pairing; this re-measures the same four
/// combinations with BenchmarkDotNet's per-operation diagnoser to confirm or refute that.
/// </summary>
[MemoryDiagnoser]
public class PairingBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private SslServerAuthenticationOptions _serverOptions = null!;
    private SslClientAuthenticationOptions _clientOptions = null!;
    private TlsContext _serverContext = null!;
    private TlsContext _clientContext = null!;

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsHarness.CreateCertificate();
        _serverOptions = TlsHarness.ServerOptions(_certificate);
        _clientOptions = TlsHarness.ClientOptions();
        _serverContext = TlsContext.CreateServer(TlsHarness.ServerOptions(_certificate));
        _clientContext = TlsContext.CreateClient(TlsHarness.ClientOptions());
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _serverContext.Dispose();
        _clientContext.Dispose();
        _certificate.Dispose();
    }

    [Benchmark(Baseline = true, Description = "SslStream server + SslStream client")]
    public async Task SslStreamBoth()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new SslStreamDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.AuthenticateAsServerAsync(_serverOptions),
            client.AuthenticateAsClientAsync(_clientOptions));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    [Benchmark(Description = "PoC server + SslStream client")]
    public async Task PocServerSslStreamClient()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new TlsSessionDuplexPipe(serverTransport);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.HandshakeAsync(_serverContext),
            client.AuthenticateAsClientAsync(_clientOptions));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    [Benchmark(Description = "SslStream server + PoC client")]
    public async Task SslStreamServerPocClient()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new SslStreamDuplexPipe(serverTransport);
        var client = new TlsSessionDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.AuthenticateAsServerAsync(_serverOptions),
            client.HandshakeAsync(_clientContext));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    [Benchmark(Description = "PoC server + PoC client")]
    public async Task PocBoth()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        var server = new TlsSessionDuplexPipe(serverTransport);
        var client = new TlsSessionDuplexPipe(clientTransport);

        await Task.WhenAll(
            server.HandshakeAsync(_serverContext),
            client.HandshakeAsync(_clientContext));

        await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
    }

    /// <summary>
    /// Floor for the PoC side: raw session driven by a minimal inline loop, no adapter.
    /// The gap to "PoC server + SslStream client" is what the pipe adapter itself costs.
    /// </summary>
    [Benchmark(Description = "raw session server + SslStream client (floor)")]
    public async Task RawSessionServerSslStreamClient()
    {
        var (clientTransport, serverTransport) = DuplexPipePair.Create();

        using var session = new TlsBufferSession();
        session.SetContext(_serverContext);
        var client = new SslStreamDuplexPipe(clientTransport);

        await Task.WhenAll(
            TlsHarness.DriveServerHandshakeAsync(session, serverTransport),
            client.AuthenticateAsClientAsync(_clientOptions));

        await client.DisposeAsync();
        await serverTransport.Output.CompleteAsync();
        await serverTransport.Input.CompleteAsync();
    }
}
