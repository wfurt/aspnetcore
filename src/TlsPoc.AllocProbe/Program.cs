// Allocation attribution: drives two TlsBufferSessions against each other entirely
// in memory, on a single thread, with preallocated buffers. GC.GetAllocatedBytesForCurrentThread
// is then exact, so every byte can be attributed to a specific phase of the handshake.
//
// No pipes, no SslStream, no Stream shim - this isolates the session itself.

using System.Collections.Concurrent;
using System.Diagnostics.Tracing;
using System.IO.Pipelines;
using System.Net.Security;
using System.Security.Authentication;
using TlsPoc.Core;

const int Warmup = 50;
const int Iterations = 200;

using var certificate = CertificateFactory.CreateSelfSigned("localhost");

var serverOptions = new SslServerAuthenticationOptions
{
    ServerCertificate = certificate,
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

using var serverContext = TlsContext.CreateServer(serverOptions);
using var clientContext = TlsContext.CreateClient(clientOptions);

// Preallocated outside all measurement windows.
var c2s = new byte[64 * 1024];
var s2c = new byte[64 * 1024];

Console.WriteLine($"Runtime: {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
Console.WriteLine($"Server GC: {System.Runtime.GCSettings.IsServerGC}");
Console.WriteLine();

for (var i = 0; i < Warmup; i++)
{
    RunHandshake(serverContext, clientContext, c2s, s2c, out _, out _, out _);
}

long totalSetup = 0, totalClient = 0, totalServer = 0, totalAll = 0;

for (var i = 0; i < Iterations; i++)
{
    var before = GC.GetAllocatedBytesForCurrentThread();
    RunHandshake(serverContext, clientContext, c2s, s2c, out var setup, out var client, out var server);
    totalAll += GC.GetAllocatedBytesForCurrentThread() - before;

    totalSetup += setup;
    totalClient += client;
    totalServer += server;
}

Console.WriteLine($"Per full handshake (client + server, both roles on one thread):");
Console.WriteLine($"  total                     : {totalAll / (double)Iterations,10:N0} B");
Console.WriteLine($"  ctor + SetContext (x2)    : {totalSetup / (double)Iterations,10:N0} B");
Console.WriteLine($"  client Handshake() calls  : {totalClient / (double)Iterations,10:N0} B");
Console.WriteLine($"  server Handshake() calls  : {totalServer / (double)Iterations,10:N0} B");
Console.WriteLine($"  unattributed (dispose,...): {(totalAll - totalSetup - totalClient - totalServer) / (double)Iterations,10:N0} B");
Console.WriteLine();

// ---------------------------------------------------------------------------------
// Part 2: the same handshake, but over the pipe plumbing the benchmarks use, so the
// harness cost can be separated from the TLS cost.
// ---------------------------------------------------------------------------------

Console.WriteLine("Per connection over IDuplexPipe plumbing (process-wide allocation):");

await MeasurePipeAsync("control: pipe pair only, no TLS", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();
    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);

    await clientStream.WriteAsync(new byte[256]);
    await clientStream.FlushAsync();

    var result = await serverTransport.Input.ReadAsync();
    serverTransport.Input.AdvanceTo(result.Buffer.End);

    await serverTransport.Output.CompleteAsync();
    await serverTransport.Input.CompleteAsync();
});

await MeasurePipeAsync("SslStream server + SslStream client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new SslStreamDuplexPipe(serverTransport);
    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
    var client = new SslStream(clientStream, leaveInnerStreamOpen: true);

    await Task.WhenAll(
        server.AuthenticateAsServerAsync(serverOptions),
        client.AuthenticateAsClientAsync(clientOptions));

    await client.DisposeAsync();
    await server.DisposeAsync();
});

await MeasurePipeAsync("PoC server + SslStream client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new TlsSessionDuplexPipe(serverTransport);
    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
    var client = new SslStream(clientStream, leaveInnerStreamOpen: true);

    await Task.WhenAll(
        server.HandshakeAsync(serverContext),
        client.AuthenticateAsClientAsync(clientOptions));

    await client.DisposeAsync();
    await server.DisposeAsync();
});

await MeasurePipeAsync("PoC server + PoC client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new TlsSessionDuplexPipe(serverTransport);
    var client = new TlsSessionDuplexPipe(clientTransport);

    await Task.WhenAll(
        server.HandshakeAsync(serverContext),
        client.HandshakeAsync(clientContext));

    await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
});

await MeasurePipeAsync("SslStream server + PoC client (mirror)", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new SslStreamDuplexPipe(serverTransport);
    var client = new TlsSessionDuplexPipe(clientTransport);

    await Task.WhenAll(
        server.AuthenticateAsServerAsync(serverOptions),
        client.HandshakeAsync(clientContext));

    await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
});

Console.WriteLine();
Console.WriteLine("Handshake only, no dispose (isolates teardown):");

await MeasurePipeAsync("SslStream server + SslStream client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new SslStreamDuplexPipe(serverTransport);
    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
    var client = new SslStream(clientStream, leaveInnerStreamOpen: true);

    await Task.WhenAll(
        server.AuthenticateAsServerAsync(serverOptions),
        client.AuthenticateAsClientAsync(clientOptions));
});

await MeasurePipeAsync("PoC server + SslStream client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();

    var server = new TlsSessionDuplexPipe(serverTransport);
    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
    var client = new SslStream(clientStream, leaveInnerStreamOpen: true);

    await Task.WhenAll(
        server.HandshakeAsync(serverContext),
        client.AuthenticateAsClientAsync(clientOptions));
});

Console.WriteLine();
Console.WriteLine("Server -> client wire shape during handshake:");
await ReportWireShapeAsync("SslStream server", async serverTransport =>
{
    var server = new SslStreamDuplexPipe(serverTransport);
    await server.AuthenticateAsServerAsync(serverOptions);
});
await ReportWireShapeAsync("PoC server", async serverTransport =>
{
    var server = new TlsSessionDuplexPipe(serverTransport);
    await server.HandshakeAsync(serverContext);
});

async Task ReportWireShapeAsync(string label, Func<IDuplexPipe, Task> runServer)
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();
    var counting = new CountingDuplexPipe(serverTransport);

    var clientStream = new DuplexPipeStream(clientTransport.Input, clientTransport.Output);
    var client = new SslStream(clientStream, leaveInnerStreamOpen: true);

    await Task.WhenAll(
        runServer(counting),
        client.AuthenticateAsClientAsync(clientOptions));

    Console.WriteLine($"  {label,-38}: {counting.Flushes} flushes, {counting.Bytes} bytes");
}

Console.WriteLine();
Console.WriteLine("Top allocated types (GCAllocationTick sampling, ~100 KB per sample):");

await ProfileAsync("PoC server + PoC client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();
    var server = new TlsSessionDuplexPipe(serverTransport);
    var client = new TlsSessionDuplexPipe(clientTransport);
    await Task.WhenAll(server.HandshakeAsync(serverContext), client.HandshakeAsync(clientContext));
    await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
});

await ProfileAsync("PoC server + SslStream client", async () =>
{
    var (clientTransport, serverTransport) = DuplexPipePair.Create();
    var server = new TlsSessionDuplexPipe(serverTransport);
    var client = new SslStreamDuplexPipe(clientTransport);
    await Task.WhenAll(server.HandshakeAsync(serverContext), client.AuthenticateAsClientAsync(clientOptions));
    await Task.WhenAll(client.DisposeAsync().AsTask(), server.DisposeAsync().AsTask());
});

static async Task ProfileAsync(string label, Func<Task> iteration)
{
    for (var i = 0; i < 20; i++)
    {
        await iteration();
    }

    using var listener = new AllocationTickListener();
    listener.Start();
    for (var i = 0; i < 400; i++)
    {
        await iteration();
    }
    listener.Stop();

    Console.WriteLine($"  {label}:");
    foreach (var (type, bytes) in listener.Top(6))
    {
        Console.WriteLine($"      {bytes,12:N0} B  {type}");
    }

    var sizes = listener.ByteArraySizes().ToList();
    if (sizes.Count > 0)
    {
        Console.WriteLine($"      byte[] sizes seen: {string.Join(", ", sizes.Select(s => $"{s.Size:N0}B x{s.Count}"))}");
    }
}

static async Task MeasurePipeAsync(string label, Func<Task> iteration)
{
    for (var i = 0; i < 30; i++)
    {
        await iteration();
    }

    const int Count = 100;
    var before = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < Count; i++)
    {
        await iteration();
    }
    var after = GC.GetTotalAllocatedBytes(precise: true);

    Console.WriteLine($"  {label,-38}: {(after - before) / (double)Count,10:N0} B");
}

static void RunHandshake(
    TlsContext serverContext,
    TlsContext clientContext,
    byte[] c2s,
    byte[] s2c,
    out long setupBytes,
    out long clientBytes,
    out long serverBytes)
{
    var mark = GC.GetAllocatedBytesForCurrentThread();

    using var client = new TlsBufferSession();
    client.SetContext(clientContext);
    using var server = new TlsBufferSession();
    server.SetContext(serverContext);

    setupBytes = GC.GetAllocatedBytesForCurrentThread() - mark;
    clientBytes = 0;
    serverBytes = 0;

    var c2sLen = 0;
    var s2cLen = 0;

    for (var round = 0; round < 32 && (!client.IsHandshakeComplete || !server.IsHandshakeComplete); round++)
    {
        mark = GC.GetAllocatedBytesForCurrentThread();
        Step(client, s2c, ref s2cLen, c2s, ref c2sLen);
        clientBytes += GC.GetAllocatedBytesForCurrentThread() - mark;

        mark = GC.GetAllocatedBytesForCurrentThread();
        Step(server, c2s, ref c2sLen, s2c, ref s2cLen);
        serverBytes += GC.GetAllocatedBytesForCurrentThread() - mark;
    }

    if (!client.IsHandshakeComplete || !server.IsHandshakeComplete)
    {
        throw new InvalidOperationException("Handshake did not converge.");
    }
}

static void Step(TlsBufferSession session, byte[] input, ref int inputLen, byte[] output, ref int outputLen)
{
    while (!session.IsHandshakeComplete)
    {
        var status = session.Handshake(
            input.AsSpan(0, inputLen),
            output.AsSpan(outputLen),
            out var consumed,
            out var produced);

        if (consumed > 0)
        {
            if (consumed < inputLen)
            {
                Buffer.BlockCopy(input, consumed, input, 0, inputLen - consumed);
            }
            inputLen -= consumed;
        }

        outputLen += produced;

        while (session.HasPendingOutput)
        {
            session.DrainPendingOutput(output.AsSpan(outputLen), out var drained);
            outputLen += drained;
        }

        switch (status)
        {
            case TlsOperationStatus.Complete:
                continue;

            case TlsOperationStatus.NeedsCertificateValidation:
                session.SetRemoteCertificateValidationResult(SslPolicyErrors.None);
                continue;

            case TlsOperationStatus.NeedMoreData:
                return;

            default:
                throw new InvalidOperationException($"Unexpected status {status}.");
        }
    }
}

/// <summary>Counts flushes and bytes written on the server -&gt; client direction.</summary>
internal sealed class CountingDuplexPipe(IDuplexPipe inner) : IDuplexPipe
{
    private readonly CountingWriter _writer = new(inner.Output);

    public PipeReader Input => inner.Input;

    public PipeWriter Output => _writer;

    public int Flushes => _writer.Flushes;

    public long Bytes => _writer.Bytes;

    private sealed class CountingWriter(PipeWriter inner) : PipeWriter
    {
        private long _pending;

        public int Flushes { get; private set; }

        public long Bytes { get; private set; }

        public override void Advance(int bytes)
        {
            _pending += bytes;
            inner.Advance(bytes);
        }

        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);

        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);

        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            if (_pending > 0)
            {
                Flushes++;
                Bytes += _pending;
                _pending = 0;
            }

            return inner.FlushAsync(cancellationToken);
        }

        public override void CancelPendingFlush() => inner.CancelPendingFlush();

        public override void Complete(Exception? exception = null) => inner.Complete(exception);

        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
    }
}

/// <summary>
/// Samples runtime GCAllocationTick events, which carry the type name that crossed each
/// ~100 KB allocation boundary. Enough samples give a reliable ranking of allocation sources.
/// </summary>
internal sealed class AllocationTickListener : EventListener
{
    private readonly ConcurrentDictionary<string, long> _bytes = new();
    private readonly ConcurrentDictionary<long, int> _byteArraySizes = new();
    private volatile bool _enabled;

    public void Start() => _enabled = true;

    public void Stop() => _enabled = false;

    public IEnumerable<(string Type, long Bytes)> Top(int count) =>
        _bytes.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).Take(count);

    public IEnumerable<(long Size, int Count)> ByteArraySizes() =>
        _byteArraySizes.Select(kv => (kv.Key, kv.Value)).OrderByDescending(x => x.Value).Take(6);

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // Keyword 0x1 == GC, Verbose is required for allocation ticks.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x1);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (!_enabled || eventData.EventName is null || !eventData.EventName.StartsWith("GCAllocationTick", StringComparison.Ordinal))
        {
            return;
        }

        var names = eventData.PayloadNames;
        if (names is null)
        {
            return;
        }

        string? typeName = null;
        long amount = 0;
        long objectSize = 0;

        for (var i = 0; i < names.Count; i++)
        {
            switch (names[i])
            {
                case "TypeName":
                    typeName = eventData.Payload?[i] as string;
                    break;
                case "AllocationAmount64":
                    amount = Convert.ToInt64(eventData.Payload?[i] ?? 0L);
                    break;
                case "AllocationAmount" when amount == 0:
                    amount = Convert.ToInt64(eventData.Payload?[i] ?? 0L);
                    break;
                case "ObjectSize":
                    objectSize = Convert.ToInt64(eventData.Payload?[i] ?? 0L);
                    break;
            }
        }

        if (!string.IsNullOrEmpty(typeName))
        {
            _bytes.AddOrUpdate(typeName, amount, (_, existing) => existing + amount);

            if (typeName == "System.Byte[]" && objectSize > 0)
            {
                _byteArraySizes.AddOrUpdate(objectSize, 1, (_, existing) => existing + 1);
            }
        }
    }
}
