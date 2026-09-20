// Server-side-only cost. Kestrel is the server, so client savings are irrelevant.
//
// The client runs async on the thread pool; the server is driven synchronously on this
// thread, so GC.GetAllocatedBytesForCurrentThread and GetThreadTimes both attribute
// exclusively to server work. Real loopback sockets, so no in-memory pipe artifacts.

using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using TlsPoc.Core;

const int Warmup = 30;
const int Connections = 200;
const int RoundTripsPerConnection = 50;
const int PayloadSize = 1024;
const int BufferSize = 64 * 1024;

using var serverCertificate = CertificateFactory.CreateSelfSigned("localhost");

// Kestrel's HttpsConnectionMiddleware resolves this once per endpoint, not per connection.
var certificateContext = SslStreamCertificateContext.Create(serverCertificate, additionalCertificates: null);

var serverOptions = new SslServerAuthenticationOptions
{
    ServerCertificateContext = certificateContext,
    EnabledSslProtocols = SslProtocols.Tls13,
    ApplicationProtocols = [SslApplicationProtocol.Http11],
    ClientCertificateRequired = false,
    AllowTlsResume = false,
};

var clientOptions = new SslClientAuthenticationOptions
{
    TargetHost = "localhost",
    EnabledSslProtocols = SslProtocols.Tls13,
    ApplicationProtocols = [SslApplicationProtocol.Http11],
    RemoteCertificateValidationCallback = (_, _, _, _) => true,
    AllowTlsResume = false,
};

using var serverContext = TlsContext.CreateServer(new SslServerAuthenticationOptions
{
    ServerCertificateContext = certificateContext,
    EnabledSslProtocols = SslProtocols.Tls13,
    ApplicationProtocols = [SslApplicationProtocol.Http11],
    ClientCertificateRequired = false,
    AllowTlsResume = false,
});

var payload = new byte[PayloadSize];
Random.Shared.NextBytes(payload);

// Preallocated outside every measurement window.
var netIn = new byte[BufferSize];
var netOut = new byte[BufferSize];
var plain = new byte[BufferSize];

Console.WriteLine($"Runtime: {RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine($"{Connections} connections x {RoundTripsPerConnection} round trips of {PayloadSize} B");
Console.WriteLine();

var sslStream = Measure(RunSslStreamServer);
var poc = Measure(RunTlsSessionServer);

Console.WriteLine("SERVER-SIDE ONLY (client cost excluded entirely)");
Console.WriteLine();
Console.WriteLine($"{"",-34}{"SslStream",14}{"TlsSession",14}{"change",12}");
Report("handshake CPU (kcycles/conn)", sslStream.HandshakeCpuUs, poc.HandshakeCpuUs);
Report("handshake wall (us/conn)", sslStream.HandshakeWallUs, poc.HandshakeWallUs);
Report("handshake alloc (B/conn)", sslStream.HandshakeBytes, poc.HandshakeBytes);
Report("round-trip CPU (kcycles/rt)", sslStream.RoundTripCpuUs, poc.RoundTripCpuUs);
Report("round-trip wall (us/rt)", sslStream.RoundTripWallUs, poc.RoundTripWallUs);
Report("round-trip alloc (B/rt)", sslStream.RoundTripBytes, poc.RoundTripBytes);

static void Report(string label, double baseline, double candidate)
{
    var change = baseline == 0 ? 0 : (candidate - baseline) / baseline * 100;
    Console.WriteLine($"{label,-34}{baseline,14:N1}{candidate,14:N1}{change,11:+0.0;-0.0;0.0}%");
}

Result Measure(ServerRunner runner)
{
    for (var i = 0; i < Warmup; i++)
    {
        RunConnection(runner, out _, out _, out _, out _, out _, out _);
    }

    long hsCpu = 0, hsWall = 0, hsBytes = 0, rtCpu = 0, rtWall = 0, rtBytes = 0;

    for (var i = 0; i < Connections; i++)
    {
        RunConnection(runner, out var h, out var hw, out var hb, out var r, out var rw, out var rb);
        hsCpu += h;
        hsWall += hw;
        hsBytes += hb;
        rtCpu += r;
        rtWall += rw;
        rtBytes += rb;
    }

    var rts = Connections * (double)RoundTripsPerConnection;
    var usPerTick = 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency;

    return new Result(
        hsCpu / 1000.0 / Connections,
        hsWall * usPerTick / Connections,
        hsBytes / (double)Connections,
        rtCpu / 1000.0 / rts,
        rtWall * usPerTick / rts,
        rtBytes / rts);
}

void RunConnection(
    ServerRunner runner,
    out long handshakeCpu,
    out long handshakeWall,
    out long handshakeBytes,
    out long roundTripCpu,
    out long roundTripWall,
    out long roundTripBytes)
{
    using var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
    listener.Listen(1);
    var endpoint = (IPEndPoint)listener.LocalEndPoint!;

    using var clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
    var connect = clientSocket.ConnectAsync(endpoint);
    using var serverSocket = listener.Accept();
    connect.GetAwaiter().GetResult();

    serverSocket.NoDelay = true;
    clientSocket.NoDelay = true;

    var clientTask = Task.Run(() => RunClientAsync(clientSocket));

    runner(serverSocket, out handshakeCpu, out handshakeWall, out handshakeBytes, out roundTripCpu, out roundTripWall, out roundTripBytes);

    clientTask.GetAwaiter().GetResult();
}

async Task RunClientAsync(Socket socket)
{
    await using var netStream = new NetworkStream(socket, ownsSocket: false);
    await using var ssl = new SslStream(netStream, leaveInnerStreamOpen: true);
    await ssl.AuthenticateAsClientAsync(clientOptions);

    var buffer = new byte[PayloadSize];
    for (var i = 0; i < RoundTripsPerConnection; i++)
    {
        await ssl.WriteAsync(payload);
        await ssl.ReadExactlyAsync(buffer);
    }
}

void RunSslStreamServer(
    Socket socket,
    out long handshakeCpu,
    out long handshakeWall,
    out long handshakeBytes,
    out long roundTripCpu,
    out long roundTripWall,
    out long roundTripBytes)
{
    var cpu0 = ThreadCpu.Ticks();
    var wall0 = System.Diagnostics.Stopwatch.GetTimestamp();
    var alloc0 = GC.GetAllocatedBytesForCurrentThread();

    var netStream = new NetworkStream(socket, ownsSocket: false);
    var ssl = new SslStream(netStream, leaveInnerStreamOpen: true);
    ssl.AuthenticateAsServer(serverOptions);

    handshakeCpu = ThreadCpu.Ticks() - cpu0;
    handshakeWall = System.Diagnostics.Stopwatch.GetTimestamp() - wall0;
    handshakeBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;

    cpu0 = ThreadCpu.Ticks();
    wall0 = System.Diagnostics.Stopwatch.GetTimestamp();
    alloc0 = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < RoundTripsPerConnection; i++)
    {
        ssl.ReadExactly(plain, 0, PayloadSize);
        ssl.Write(plain, 0, PayloadSize);
    }

    roundTripCpu = ThreadCpu.Ticks() - cpu0;
    roundTripWall = System.Diagnostics.Stopwatch.GetTimestamp() - wall0;
    roundTripBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;

    ssl.Dispose();
    netStream.Dispose();
}

void RunTlsSessionServer(
    Socket socket,
    out long handshakeCpu,
    out long handshakeWall,
    out long handshakeBytes,
    out long roundTripCpu,
    out long roundTripWall,
    out long roundTripBytes)
{
    var inLen = 0;

    var cpu0 = ThreadCpu.Ticks();
    var wall0 = System.Diagnostics.Stopwatch.GetTimestamp();
    var alloc0 = GC.GetAllocatedBytesForCurrentThread();

    var session = new TlsBufferSession();
    session.SetContext(serverContext);
    Handshake(session, socket, netIn, ref inLen, netOut);

    handshakeCpu = ThreadCpu.Ticks() - cpu0;
    handshakeWall = System.Diagnostics.Stopwatch.GetTimestamp() - wall0;
    handshakeBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;

    cpu0 = ThreadCpu.Ticks();
    wall0 = System.Diagnostics.Stopwatch.GetTimestamp();
    alloc0 = GC.GetAllocatedBytesForCurrentThread();

    for (var i = 0; i < RoundTripsPerConnection; i++)
    {
        ReadExactly(session, socket, netIn, ref inLen, plain, PayloadSize);
        WriteAll(session, socket, plain.AsSpan(0, PayloadSize), netOut);
    }

    roundTripCpu = ThreadCpu.Ticks() - cpu0;
    roundTripWall = System.Diagnostics.Stopwatch.GetTimestamp() - wall0;
    roundTripBytes = GC.GetAllocatedBytesForCurrentThread() - alloc0;

    session.Dispose();
}

static void Handshake(TlsBufferSession session, Socket socket, byte[] netIn, ref int inLen, byte[] netOut)
{
    while (!session.IsHandshakeComplete)
    {
        var status = session.Handshake(netIn.AsSpan(0, inLen), netOut, out var consumed, out var produced);
        Consume(netIn, ref inLen, consumed);

        if (produced > 0)
        {
            SendAll(socket, netOut, produced);
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
                continue;

            case TlsOperationStatus.NeedsCertificateValidation:
                session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
                continue;

            case TlsOperationStatus.DestinationTooSmall:
                Drain(session, socket, netOut);
                continue;

            case TlsOperationStatus.NeedMoreData:
                inLen += Receive(socket, netIn, inLen);
                continue;

            default:
                throw new InvalidOperationException($"Unexpected handshake status {status}.");
        }
    }
}

static void ReadExactly(TlsBufferSession session, Socket socket, byte[] netIn, ref int inLen, byte[] plain, int count)
{
    var got = 0;
    while (got < count)
    {
        var status = session.Read(netIn.AsSpan(0, inLen), plain.AsSpan(got), out var consumed, out var produced);
        Consume(netIn, ref inLen, consumed);
        got += produced;

        if (got >= count)
        {
            return;
        }

        if (status == TlsOperationStatus.NeedMoreData || (consumed == 0 && produced == 0))
        {
            inLen += Receive(socket, netIn, inLen);
        }
    }
}

static void WriteAll(TlsBufferSession session, Socket socket, ReadOnlySpan<byte> data, byte[] netOut)
{
    while (!data.IsEmpty)
    {
        var status = session.Write(data, netOut, out var consumed, out var produced);
        if (produced > 0)
        {
            SendAll(socket, netOut, produced);
        }
        data = data[consumed..];

        if (status == TlsOperationStatus.DestinationTooSmall)
        {
            Drain(session, socket, netOut);
        }
    }
}

static void Drain(TlsBufferSession session, Socket socket, byte[] netOut)
{
    while (session.HasPendingOutput)
    {
        var status = session.DrainPendingOutput(netOut, out var written);
        if (written > 0)
        {
            SendAll(socket, netOut, written);
        }
        if (written == 0 || status != TlsOperationStatus.DestinationTooSmall)
        {
            return;
        }
    }
}

static void Consume(byte[] buffer, ref int length, int consumed)
{
    if (consumed <= 0)
    {
        return;
    }

    if (consumed < length)
    {
        Buffer.BlockCopy(buffer, consumed, buffer, 0, length - consumed);
    }
    length -= consumed;
}

static int Receive(Socket socket, byte[] buffer, int offset)
{
    var n = socket.Receive(buffer, offset, buffer.Length - offset, SocketFlags.None);
    if (n == 0)
    {
        throw new IOException("Peer closed the connection.");
    }
    return n;
}

static void SendAll(Socket socket, byte[] buffer, int count)
{
    var sent = 0;
    while (sent < count)
    {
        sent += socket.Send(buffer, sent, count - sent, SocketFlags.None);
    }
}

internal delegate void ServerRunner(
    Socket socket,
    out long handshakeCpu,
    out long handshakeWall,
    out long handshakeBytes,
    out long roundTripCpu,
    out long roundTripWall,
    out long roundTripBytes);

internal readonly record struct Result(
    double HandshakeCpuUs,
    double HandshakeWallUs,
    double HandshakeBytes,
    double RoundTripCpuUs,
    double RoundTripWallUs,
    double RoundTripBytes);

/// <summary>
/// CPU cycles consumed by the calling thread. GetThreadTimes only has 15.625 ms
/// granularity, which is far too coarse for a sub-millisecond handshake.
/// </summary>
internal static partial class ThreadCpu
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryThreadCycleTime(IntPtr thread, out ulong cycleTime);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr GetCurrentThread();

    public static long Ticks()
    {
        if (!QueryThreadCycleTime(GetCurrentThread(), out var cycles))
        {
            throw new InvalidOperationException("QueryThreadCycleTime failed.");
        }
        return (long)cycles;
    }
}
