using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using BenchmarkDotNet.Attributes;

namespace TlsPoc.Benchmarks;

/// <summary>
/// Isolates the fixed per-session setup cost with no handshake and no I/O at all.
///
/// TlsContext.CreateServer/CreateClient produce a non-wedge context, so every
/// TlsSession.SetContext clones the whole SslAuthenticationOptions bag. SslStream's
/// internal TlsContext.WrapShared shares the bag by reference instead. If the clone is
/// the source of the per-connection allocation, a richer options bag must cost more.
/// </summary>
[MemoryDiagnoser]
public class SessionSetupBenchmarks
{
    private X509Certificate2 _certificate = null!;
    private TlsContext _bareContext = null!;
    private TlsContext _alpnContext = null!;
    private TlsContext _chainPolicyContext = null!;

    [GlobalSetup]
    public void Setup()
    {
        _certificate = TlsHarness.CreateCertificate();

        _bareContext = TlsContext.CreateServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
        });

        _alpnContext = TlsContext.CreateServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
        });

        _chainPolicyContext = TlsContext.CreateServer(new SslServerAuthenticationOptions
        {
            ServerCertificate = _certificate,
            EnabledSslProtocols = SslProtocols.Tls13,
            ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
            CertificateChainPolicy = new X509ChainPolicy(),
        });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _bareContext.Dispose();
        _alpnContext.Dispose();
        _chainPolicyContext.Dispose();
        _certificate.Dispose();
    }

    [Benchmark(Baseline = true, Description = "new TlsBufferSession() only (no context)")]
    public void SessionOnly()
    {
        using var session = new TlsBufferSession();
    }

    [Benchmark(Description = "SetContext, cert only")]
    public void SetContextBare()
    {
        using var session = new TlsBufferSession();
        session.SetContext(_bareContext);
    }

    [Benchmark(Description = "SetContext, cert + protocols + ALPN")]
    public void SetContextAlpn()
    {
        using var session = new TlsBufferSession();
        session.SetContext(_alpnContext);
    }

    [Benchmark(Description = "SetContext, + X509ChainPolicy")]
    public void SetContextChainPolicy()
    {
        using var session = new TlsBufferSession();
        session.SetContext(_chainPolicyContext);
    }
}
