// Minimal load driver: holds N concurrent HTTPS connections open against the benchmark
// server so a GC heap dump can be captured while the connections are live.
//
// usage: TlsPoc.LoadClient <url> <connections> <seconds>

using System.Diagnostics;
using System.Net.Security;

var url = args.Length > 0 ? args[0] : "https://localhost:5099/";
var connections = args.Length > 1 ? int.Parse(args[1]) : 256;
var seconds = args.Length > 2 ? int.Parse(args[2]) : 30;

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = connections,
    PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
    PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan,
    SslOptions = new SslClientAuthenticationOptions
    {
        RemoteCertificateValidationCallback = (_, _, _, _) => true,
    },
};

using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
long total = 0;
var sw = Stopwatch.StartNew();

var workers = Enumerable.Range(0, connections).Select(async _ =>
{
    while (!cts.IsCancellationRequested)
    {
        try
        {
            using var response = await http.GetAsync(url, cts.Token);
            await response.Content.ReadAsByteArrayAsync(cts.Token);
            Interlocked.Increment(ref total);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return;
        }
    }
});

await Task.WhenAll(workers);
sw.Stop();

Console.WriteLine($"requests={total} in {sw.Elapsed.TotalSeconds:F1}s => {total / sw.Elapsed.TotalSeconds:N0} rps");
