// Correctness probe for the PoC: a TlsSessionDuplexPipe server (new .NET 11 sans-IO
// TLS API) handshaking against a real SslStream client over an in-memory duplex pipe,
// including SNI-deferred context selection and application-data round trips.

using System.Buffers;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using TlsPoc.Core;

Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"OS:      {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
Console.WriteLine();

using var certificate = CertificateFactory.CreateSelfSigned("localhost");

// Kestrel would build these once per endpoint, not per connection.
using var bootstrapContext = TlsContext.CreateServer(new SslServerAuthenticationOptions());
using var hostContext = TlsContext.CreateServer(new SslServerAuthenticationOptions
{
    ServerCertificate = certificate,
    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
    ClientCertificateRequired = false,
});

var (clientTransport, serverTransport) = DuplexPipePair.Create();

string? observedSni = null;
var selectorCalls = 0;

var server = new TlsSessionDuplexPipe(serverTransport);
var serverHandshake = server.HandshakeAsync(bootstrapContext, hello =>
{
    selectorCalls++;
    observedSni = hello.ServerName;
    return hostContext;
});

var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
using var client = new SslStream(clientStream, leaveInnerStreamOpen: true, (_, _, _, _) => true);

var sw = Stopwatch.StartNew();
var clientHandshake = client.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
{
    TargetHost = "localhost",
    EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12,
    ApplicationProtocols = [SslApplicationProtocol.Http2, SslApplicationProtocol.Http11],
});

await Task.WhenAll(serverHandshake, clientHandshake).WaitAsync(TimeSpan.FromSeconds(30));
sw.Stop();

Console.WriteLine($"Handshake completed in {sw.Elapsed.TotalMilliseconds:F2} ms");
Console.WriteLine($"  SNI selector calls : {selectorCalls} (observed '{observedSni}')");
Console.WriteLine($"  server protocol    : {server.Session.NegotiatedProtocol}");
Console.WriteLine($"  server cipher suite: {server.Session.NegotiatedCipherSuite}");
Console.WriteLine($"  server ALPN        : {server.Session.NegotiatedApplicationProtocol}");
Console.WriteLine($"  client protocol    : {client.SslProtocol}");
Console.WriteLine($"  client ALPN        : {client.NegotiatedApplicationProtocol}");
Console.WriteLine();

var request = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n"u8.ToArray();
var response = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok"u8.ToArray();

await client.WriteAsync(request);
await client.FlushAsync();

var received = await ReadExactlyAsync(server.Input, request.Length);
Console.WriteLine($"server decrypted {received.Length} bytes: {Escape(received)}");

server.Output.Write(response);
await server.Output.FlushAsync();

var echoed = new byte[response.Length];
await client.ReadExactlyAsync(echoed);
Console.WriteLine($"client decrypted {echoed.Length} bytes: {Escape(echoed)}");

var ok = received.AsSpan().SequenceEqual(request) && echoed.AsSpan().SequenceEqual(response);

// Larger payload: exercises multi-record fragmentation and multi-segment sequences.
var big = new byte[512 * 1024];
Random.Shared.NextBytes(big);

var writeTask = Task.Run(async () =>
{
    await client.WriteAsync(big);
    await client.FlushAsync();
});

var bigReceived = await ReadExactlyAsync(server.Input, big.Length);
await writeTask;

var bigOk = bigReceived.AsSpan().SequenceEqual(big);
ok &= bigOk;
Console.WriteLine($"512 KiB client -> server: {(bigOk ? "match" : "MISMATCH")}");

// Server -> client direction with a payload spanning many TLS records.
var bigResponse = new byte[512 * 1024];
Random.Shared.NextBytes(bigResponse);

var serverWrite = Task.Run(async () =>
{
    server.Output.Write(bigResponse);
    await server.Output.FlushAsync();
});

var bigEchoed = new byte[bigResponse.Length];
await client.ReadExactlyAsync(bigEchoed);
await serverWrite;

var bigResponseOk = bigEchoed.AsSpan().SequenceEqual(bigResponse);
ok &= bigResponseOk;
Console.WriteLine($"512 KiB server -> client: {(bigResponseOk ? "match" : "MISMATCH")}");

Console.WriteLine();
Console.WriteLine(ok ? "PROBE PASSED" : "PROBE FAILED");

return ok ? 0 : 1;

static async Task<byte[]> ReadExactlyAsync(PipeReader reader, int count)
{
    while (true)
    {
        var result = await reader.ReadAsync();
        if (result.Buffer.Length >= count)
        {
            var slice = result.Buffer.Slice(0, count);
            var bytes = slice.ToArray();
            reader.AdvanceTo(slice.End);
            return bytes;
        }

        if (result.IsCompleted)
        {
            throw new InvalidOperationException($"Stream ended after {result.Buffer.Length} of {count} bytes.");
        }

        reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
    }
}

static string Escape(byte[] bytes) =>
    Encoding.UTF8.GetString(bytes).Replace("\r", "\\r").Replace("\n", "\\n");
