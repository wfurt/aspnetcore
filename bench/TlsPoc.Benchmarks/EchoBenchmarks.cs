using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;

namespace TlsPoc.Benchmarks;

/// <summary>
/// Steady-state application-data cost: a request/response round trip over an already
/// established connection, which is the path every Kestrel request pays.
///
/// Both endpoints use the same stack here, so the measured delta is roughly twice the
/// per-side saving.
/// </summary>
[MemoryDiagnoser]
public class EchoBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private TlsContext _serverContext = null!;
    private TlsContext _clientContext = null!;

    private SslStreamPair _sslStreamPair = null!;
    private TlsSessionPair _tlsSessionPair = null!;
    private byte[] _payload = null!;

    [Params(256, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsHarness.CreateCertificate();
        _payload = new byte[PayloadSize];
        Random.Shared.NextBytes(_payload);

        _serverContext = TlsContext.CreateServer(TlsHarness.ServerOptions(_certificate));
        _clientContext = TlsContext.CreateClient(TlsHarness.ClientOptions());

        _sslStreamPair = TlsHarness.ConnectSslStreamAsync(
            TlsHarness.ServerOptions(_certificate),
            TlsHarness.ClientOptions()).GetAwaiter().GetResult();

        _tlsSessionPair = TlsHarness.ConnectTlsSessionAsync(_serverContext, _clientContext)
            .GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _sslStreamPair.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _tlsSessionPair.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _serverContext.Dispose();
        _clientContext.Dispose();
        _certificate.Dispose();
    }

    [Benchmark(Baseline = true, Description = "SslStream over DuplexPipeStream (Kestrel today)")]
    public ValueTask SslStreamRoundTrip() =>
        TlsHarness.RoundTripAsync(_sslStreamPair.Client, _sslStreamPair.Server, _payload);

    [Benchmark(Description = "TlsBufferSession direct on pipes (PoC)")]
    public ValueTask TlsSessionRoundTrip() =>
        TlsHarness.RoundTripAsync(_tlsSessionPair.Client, _tlsSessionPair.Server, _payload);
}
