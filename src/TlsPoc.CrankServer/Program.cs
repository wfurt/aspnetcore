// Real Kestrel serving HTTPS, with the TLS layer switchable between Kestrel's own
// UseHttps (SslStream) and the PoC's TlsSessionDuplexPipe, so crank can load-test both.
//
//   TLS_MODE=sslstream   -> listenOptions.UseHttps(...)          (Kestrel today)
//   TLS_MODE=tlssession  -> custom connection middleware on TlsBufferSession

using System.Net.Security;
using System.Text.Json.Serialization;
using System.Security.Authentication;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using TlsPoc.Core;

var mode = Environment.GetEnvironmentVariable("TLS_MODE") ?? "sslstream";
var port = int.TryParse(Environment.GetEnvironmentVariable("SERVER_PORT"), out var p) ? p : 5000;

// Loopback by default so local runs don't trigger a Windows Firewall prompt on every
// republish; the perf lab drives load from a separate machine and needs "any".
var bindAny = string.Equals(Environment.GetEnvironmentVariable("SERVER_BIND"), "any", StringComparison.OrdinalIgnoreCase);
var perConnectionCtx = Environment.GetEnvironmentVariable("TLS_CTX_PER_CONN") == "1";

var certificate = CertificateFactory.CreateSelfSigned(
    "localhost",
    algorithm: Environment.GetEnvironmentVariable("CERT_ALG") ?? "ecdsap256");

// Resolved once per endpoint, exactly as Kestrel's HttpsConnectionMiddleware does.
var certificateContext = SslStreamCertificateContext.Create(certificate, additionalCertificates: null);

var serverOptions = new SslServerAuthenticationOptions
{
    ServerCertificateContext = certificateContext,
    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
    ApplicationProtocols = [SslApplicationProtocol.Http11],
    ClientCertificateRequired = false,
};

// A "Connection: close" benchmark only measures handshake cost if sessions cannot be
// resumed - a resumed handshake does no signature at all, which is why the certificate
// algorithm otherwise makes almost no difference. tls-handshakes-kestrel in
// aspnet/Benchmarks disables resumption for the same reason.
var allowTlsResume = Environment.GetEnvironmentVariable("TLS_RESUME") != "0";
serverOptions.AllowTlsResume = allowTlsResume;

TlsContext? tlsContext = mode == "tlssession" ? TlsContext.CreateServer(serverOptions) : null;

// Diagnostic only: shard connections over N contexts to see whether per-record work
// contends on state shared inside a single TlsContext/SSL_CTX.
var shardCount = int.TryParse(Environment.GetEnvironmentVariable("TLS_CTX_SHARDS"), out var s) && s > 1 ? s : 0;
TlsContext[]? shards = null;
if (mode == "tlssession" && shardCount > 0)
{
    shards = new TlsContext[shardCount];
    for (var i = 0; i < shardCount; i++)
    {
        shards[i] = TlsContext.CreateServer(serverOptions);
    }
}

var shardCursor = 0;

var builder = WebApplication.CreateSlimBuilder(args);

// Runs transport pipe continuations on the epoll/IOCP thread instead of queueing them.
if (Environment.GetEnvironmentVariable("TLS_INLINE_SCHED") == "1")
{
    builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(
        o => o.UnsafePreferInlineScheduling = true);
}

// Number of epoll engine threads; the PoC decrypts inline on these, so it bounds parallelism.
if (int.TryParse(Environment.GetEnvironmentVariable("TLS_IOQ"), out var ioq) && ioq > 0)
{
    builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportOptions>(
        o => o.IOQueueCount = ioq);
}

// LOG=1 keeps Kestrel's warnings/errors visible; otherwise logging is off for benchmarking.
if (Environment.GetEnvironmentVariable("LOG") == "1")
{
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
}
else
{
    builder.Logging.ClearProviders();
}

builder.WebHost.ConfigureKestrel(options =>
{
    if (bindAny)
    {
        options.ListenAnyIP(port, ConfigureEndpoint);
    }
    else
    {
        options.ListenLocalhost(port, ConfigureEndpoint);
    }

    void ConfigureEndpoint(ListenOptions listen)
    {
        listen.Protocols = HttpProtocols.Http1;

        if (mode == "sslstream")
        {
            listen.UseHttps(httpsOptions =>
            {
                // Kestrel resolves this into an SslStreamCertificateContext once per endpoint.
                httpsOptions.ServerCertificate = certificate;
                httpsOptions.SslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12;

                if (!allowTlsResume)
                {
                    httpsOptions.OnAuthenticate = (_, sslOptions) => sslOptions.AllowTlsResume = false;
                }
            });
        }
        else if (mode == "sslpipe")
        {
            // SslStream inside the SAME custom middleware, to separate the cost of the
            // middleware/IDuplexPipe swap from the cost of TlsBufferSession.
            listen.Use(next => async connection =>
            {
                var tls = new SslStreamDuplexPipe(connection.Transport);
                var original = connection.Transport;

                try
                {
                    await tls.AuthenticateAsServerAsync(serverOptions);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[sslpipe] handshake failed: {ex.GetType().Name}: {ex.Message}");
                    connection.Abort();
                    return;
                }

                connection.Transport = tls;

                try
                {
                    await next(connection);
                }
                finally
                {
                    connection.Transport = original;
                    await tls.DisposeAsync();
                }
            });
        }
        else
        {
            listen.Use(next => async connection =>
            {
                // TLS_CTX_PER_CONN=1 isolates the shared-TlsContext variable: each connection
                // gets its own context instead of sharing one per endpoint.
                var perConnectionContext = perConnectionCtx ? TlsContext.CreateServer(serverOptions) : null;
                var shared = shards is null
                    ? tlsContext!
                    : shards[(uint)Interlocked.Increment(ref shardCursor) % shards.Length];
                var tls = new TlsSessionDuplexPipe(connection.Transport);
                var original = connection.Transport;

                try
                {
                    await tls.HandshakeAsync(perConnectionContext ?? shared);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tls] handshake failed: {ex.GetType().Name}: {ex.Message}");
                    connection.Abort();
                    perConnectionContext?.Dispose();
                    return;
                }

                connection.Transport = tls;

                try
                {
                    await next(connection);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[tls] connection failed: {ex.GetType().Name}: {ex.Message}");
                    throw;
                }
                finally
                {
                    connection.Transport = original;
                    await tls.DisposeAsync();
                    perConnectionContext?.Dispose();
                }
            });
        }
    }
});

var app = builder.Build();

// RESPONSE_SIZE lets the response body be sized so the benchmark can separate per-request
// overhead (which is what this PoC removes) from bulk record throughput, where the AEAD
// cost dominates and the TLS layer's own overhead matters much less.
var responseSize = int.TryParse(Environment.GetEnvironmentVariable("RESPONSE_SIZE"), out var rs) && rs > 0 ? rs : 0;

if (responseSize > 0)
{
    var payload = new byte[responseSize];
    Random.Shared.NextBytes(payload);

    app.MapGet("/", (HttpContext context) =>
    {
        context.Response.ContentType = "application/octet-stream";
        context.Response.ContentLength = payload.Length;
        return context.Response.Body.WriteAsync(payload, 0, payload.Length);
    });
}
else
{
    app.MapGet("/", () => "Hello, World!");
}

// Path and payload of the TechEmpower plaintext benchmark, so this server can be dropped
// into the standard `plaintext` scenarios from aspnet/Benchmarks with
// --application.source.localFolder. That keeps their load configuration (wrk, pipelining,
// headers) while letting TLS_MODE choose the TLS layer, which their app cannot do.
app.MapGet("/plaintext", () => Results.Text("Hello, World!", "text/plain"));

// Likewise for the standard `json` scenarios. Serialisation is real (source-generated) so
// the per-request work matches theirs rather than being shortcut to a constant.
app.MapGet("/json", (HttpContext context) =>
    context.Response.WriteAsJsonAsync(
        new JsonMessage { message = "Hello, World!" },
        AppJsonContext.Default.JsonMessage));

// Correctness endpoints (not used by any benchmark). These exercise paths the plaintext
// benchmarks never touch: a large request body read through the PipeReader, and a
// chunked response with no Content-Length written through the PipeWriter.
app.MapPost("/echo", async (HttpContext context) =>
{
    var total = 0L;
    uint hash = 2166136261;
    var buffer = new byte[16 * 1024];
    int read;

    while ((read = await context.Request.Body.ReadAsync(buffer)) > 0)
    {
        total += read;
        for (var i = 0; i < read; i++)
        {
            hash = (hash ^ buffer[i]) * 16777619;
        }
    }

    return Results.Text($"{total}:{hash}", "text/plain");
});

app.MapGet("/chunked", async (HttpContext context) =>
{
    context.Response.ContentType = "text/plain";
    for (var i = 0; i < 64; i++)
    {
        await context.Response.WriteAsync(new string('x', 1024));
        await context.Response.Body.FlushAsync();
    }
});

// Crank waits on this text, so it must not be printed until the port is actually open.
app.Lifetime.ApplicationStarted.Register(() =>
    Console.WriteLine($"Application started. TLS mode: {mode}, port: {port}, bindAny: {bindAny}"));

app.Run();

/// <summary>Payload of the TechEmpower json benchmark.</summary>
internal sealed class JsonMessage
{
    public string message { get; set; } = string.Empty;
}

[JsonSerializable(typeof(JsonMessage))]
internal partial class AppJsonContext : JsonSerializerContext;
