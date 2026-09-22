# Kestrel on .NET 11 sans-IO TLS — PoC status and handoff

PoC replacing Kestrel's `SslStream` TLS layer with the .NET 11 experimental sans-IO
primitives (`TlsContext` / `TlsBufferSession`, `SYSLIB5007`), to see whether skipping
the `Stream` shim is worth anything in CPU, memory or RPS.

Kestrel today:   transport pipe -> DuplexPipeStream -> SslStream -> StreamPipeReader/Writer -> app
This PoC:        transport pipe -> TlsSessionDuplexPipe (decrypt/encrypt inline) -> app

---

## Where this is now — two branches

**`kestrel-tlscontext-poc`** (this branch, `wfurt/aspnetcore`)
Standalone prototype plus every measurement in this document. Not for a PR: it is
scaffolding, and the benchmark harness here exists to A/B two TLS layers in one process,
which no standard benchmark can do. Read this document for the numbers and the method.

**`kestrel-sansio-tls`** (`wfurt/aspnetcore`, branched from upstream `main` at `60a812ff`)
The real integration into `HttpsConnectionMiddleware`, 10 commits. Contains:

* `Internal/TlsSessionDuplexPipe.cs` - the adapter, ported from here
* `Internal/SansIoTlsSupport.cs` - platform allow-list (Windows/Linux) + capability probe +
  opt-in `AppContext` switch `Microsoft.AspNetCore.Server.Kestrel.EnableSansIoTls`
* `Internal/TlsCipherSuiteDecomposition.cs` - legacy cipher mapping copied from the runtime,
  validated against `SslStream`; wants to move to the runtime source-sharing contract
* `Internal/TlsConnectionFeature.cs` - session-backed constructor
* `Middleware/HttpsConnectionMiddleware.cs` - the sans-IO connection path

### State of that branch

**Update.** The renegotiation blocker was not a defect in the adapter. It was the runtime
the branch is pinned to. See "The renegotiation blocker was a runtime pin" below.

| suite (sans-IO switch on) | result |
|---|---|
| `InMemory.FunctionalTests` (whole assembly) | **1655 passed, 1 failed** |
| `Sockets.FunctionalTests` | **92 passed, 0 failed** |
| `Interop.FunctionalTests` | **447 passed, 1 failed** |
| `HttpsConnectionMiddlewareTests`, switch off | **55 passed, 0 failed** - default path unaffected |

The 2 remaining failures are the same gap: `SslStreamIsAvailable` and
`Http2RequestTests.GET_Metrics_HttpProtocolAndTlsSet` both read `ISslStreamFeature.SslStream`,
which this path cannot supply. Both pass on the default path.

**HTTP/2 is now covered** and works: 447 of 448 Interop tests pass on the sans-IO path,
including the full h2spec suite. The "HTTP/2 is never exercised" gap recorded under
"Correctness verification" is closed.

### The renegotiation blocker was a runtime pin

`RequestClientCertificate` is documented to re-arm the handshake state machine so the caller
drives the second handshake via `Handshake()`. The instrumentation this document asked for
produced the tell on the first try:

```
[PHA] RequestClientCertificate status=Complete written=99 pendingOut=False complete=True
[PHA] Handshake in=0 status=Complete consumed=0 written=0 complete=True
[PHA] done remoteCert=<null>
```

`complete=True` after the request means the session never re-armed, so `Handshake` took the
"already complete" short-circuit and never read the client's certificate.

The re-arm is guarded by `_postHandshakeAuthActive` in `RequestClientCertificateBufferedCore`.
That field does not exist in the runtime this branch restores:

| runtime | `_postHandshakeAuthActive` |
|---|---|
| `11.0.0-rc.1.26453.108` (what the branch pins, via upstream `main`) | absent |
| `11.0.0-rc.2.26471.109` | present |

The prototype in this repo pins **RC2** in its `global.json`; the Kestrel branch inherits
upstream main's **RC1** transport pin. The adapter code was correct the whole time - all four
renegotiation tests pass unchanged on RC2. The three speculative rewrites could not have
worked in that tree.

Until upstream flows RC2, run the tests with roll-forward:

```bash
DOTNET_ROLL_FORWARD=LatestMinor DOTNET_ROLL_FORWARD_TO_PRERELEASE=1 \
  ./.dotnet/dotnet test ... -p:EnableSansIoTls=true
```

The same pin means **no integrated-path benchmarking is meaningful in that tree** without the
override either.

### Defects found and fixed since

1. **Busy spin when the peer closes mid-record.** A client that sends a partial record - or an
   alert - and disconnects left every loop driving the session spinning: `ReadAsync` returns
   the same completed buffer forever, the session cannot consume it, and `buffer.IsEmpty` is
   never true. In the handshake this burned a core until the handshake timeout and then
   reported the connection as a *timeout* rather than a failure.
   `ClientAttemptingToUseUnsupportedProtocolIsLoggedAsDebug` is exactly this case; tracing one
   run of it produced 91 MB of output. Fixed in the handshake, the post-handshake exchange and
   the reader.
2. **A rejected client certificate looked like a successful handshake.** Recording a rejected
   validation result does not fail the call that records it - it makes the *next* session
   operation throw. The loop exited on `IsHandshakeComplete` before that happened, so the
   connection was reported as established, tagged with a negotiated protocol and handed to the
   application. Caught by `Http2Connection_TlsError`.
3. **`OnAuthenticate` ran once per endpoint, not once per connection**, because the sans-IO
   path caches one `TlsContext` and that callback is invoked while building it. The first
   connection's options silently applied to every later connection. That configuration now
   stays on `SslStream`.
4. **No `TlsHandshakeStart` event.** The path reported `TlsHandshakeStop` and
   `TlsHandshakeFailed` without a matching start.
5. **Drain-guard message mismatch** - the tests assert `SslStream`'s exact wording.
6. **Dead buffer rental** in `RequestClientCertificateAsync`.

### Integrated-path benchmark — the lab, by core count

The re-benchmark this document asks for, finally run against Kestrel built with the change
rather than against the prototype. Lab access works without VPN: the relay profiles
authenticate through the Azure CLI, so `--profile aspnet-gold-lin-relay --relay` with an
`az login` session is enough.

**How the integrated build gets into the lab.** crank publishes self-contained by default, so
every framework assembly is app-local and `options.outputFiles` can overlay the Kestrel
assembly built from the integration branch on top of the published output. The app sets the
AppContext switch from `TLS_MODE` before the first connection is accepted, which is what makes
a same-binary A/B possible; no standard scenario can do this, because the app behind it can
only ever construct `SslStream`. The lab installs **rc.2.26471.109**, so the post-handshake
re-arm is present there even though the branch's own restore pins RC1.

Both scenarios use `wrk` with `pipeline: 16` and the plaintext preset, matching the load
configuration the standard `plaintext https` scenario uses. This matters more than it looks:
an earlier sweep with non-pipelined bombardier topped out near 600k rps, which capped the
*load generator* before 28 cores and buried the TLS difference in the harness. Absolute
numbers are higher than the table further down because this app is a bare 13-byte endpoint
rather than the TechEmpower plaintext app; read the deltas, not the totals.

Every run verified which layer actually served, via a `/tlsinfo` self-check printed at
startup - `selfcheck layer=sansio protocol=Tls13`. Without that a sweep can compare
`SslStream` against itself at every core point and still report a plausible curve.

#### Linux (`aspnet-gold-lin`, 56 cores)

| Server cores | SslStream | sans-IO | Δ |
|---|---:|---:|---:|
| 4  | 1,126,631 | 1,193,505 | **+5.9%** |
| 8  | 1,894,691 | 2,046,188 | **+8.0%** |
| 16 | 3,179,998 | 3,401,048 | **+7.0%** |
| 28 | 4,611,237 | 4,552,745 | −1.3% |
| 56 (whole machine) | 4,345,821 | 4,379,343 | +0.8% |

#### Windows (`aspnet-gold-win`, 56 cores)

| Server cores | SslStream | sans-IO | Δ |
|---|---:|---:|---:|
| 4  |   840,694 |   904,418 | **+7.6%** |
| 8  | 1,582,073 | 1,650,697 | **+4.3%** |
| 16 | 2,768,876 | 2,858,821 | **+3.2%** |
| 28 | 4,420,929 | 4,542,828 | **+2.8%** |
| 56 (whole machine) | 6,062,567 | 6,082,285 | +0.3% |

Single runs except Linux 28, which is a median of 3. Zero bad responses everywhere.

**The shape holds: the win is real where the server is CPU-bound and compresses to nothing as
the machine approaches its ceiling.** Windows declines monotonically, +7.6% at 4 cores down to
+0.3% at 56. Linux gains +6-8% through 16 cores and then flattens.

**The Linux 28 and 56 rows are a ceiling, not a regression.** Linux peaks near 28 cores on this
workload and 56 cores is *slower than 28 for both layers* (4,345,821 vs 4,611,237), exactly the
non-scaling this document recorded before - it simply arrives earlier here because a bare
13-byte endpoint reaches the ceiling with fewer cores than the TechEmpower app does. At 28
cores the point does not replicate in either direction: three pairs gave −2.9%, −1.3% and
+3.1%, a six-point spread around zero, which is the run-to-run drift this document already
warns about at high core counts. Quote it as "within noise", not as a loss.

### Memory — per-connection cost is real, and now measurable

The earlier per-connection claim was retracted because RSS could not resolve it: the same
5,000-connection point varied by 20 KB/connection across identical runs. Managed heap after a
forced collection does resolve it. Measured with N connections that each served a request and
then went idle, so the adapter's return-buffers-when-idle path has already run:

| idle connections | SslStream | sans-IO | difference |
|---|---:|---:|---:|
| 2,000 | 14,133 B/conn | 17,987 B/conn | **+27%** |
| 5,000 |  8,311 B/conn | 10,215 B/conn | **+23%** |

The absolute figures move with N and GC timing; the ~23-27% gap does not. It corroborates the
lab runs, where the sans-IO application's working set was higher in **all ten** measurements,
by 15-51 MB.

So the trade is a CPU win for a per-connection memory cost. That is worth stating plainly in
the PR rather than leaving for a reviewer to find, and it is the one number that argues
against defaulting this on for connection-heavy workloads.

### Working in that tree

```bash
git clone --depth 1 --branch main https://github.com/dotnet/aspnetcore.git
cd aspnetcore && ./restore.sh                     # ~2 min, puts an SDK in ./.dotnet
./.dotnet/dotnet build src/Servers/Kestrel/Core/src/Microsoft.AspNetCore.Server.Kestrel.Core.csproj -c Release   # ~16 s
./.dotnet/dotnet test src/Servers/Kestrel/test/InMemory.FunctionalTests/InMemory.FunctionalTests.csproj \
    -c Release --filter "FullyQualifiedName~HttpsConnectionMiddlewareTests"                                      # ~4 s
```

The test projects now take the switch as a build property, so no hand-editing is needed:

```bash
./.dotnet/dotnet test ... -p:EnableSansIoTls=true
```

### Two runtime issues to file

1. **Bug.** Peer-sent intermediates never reach `X509Chain.ChainPolicy.ExtraStore`, so a
   validation callback sees an empty extra store where `SslStream` gives it the chain the peer
   sent. Confirmed still present on `11.0.0-rc.2.26471.109`: same client certificate, same
   chain, `ExtraStore` count **0 on the sans-IO path vs 3 under `SslStream`**.

   File it against the *capture*, not against `AcceptWithDefaultValidation`, which does seed
   the store correctly:

   ```csharp
   if (_externalRemoteCertificates is { Count: > 0 } intermediates)
       chain.ChainPolicy.ExtraStore.AddRange(intermediates);
   ```

   The field feeding it is populated in `CaptureRemoteCertificateForExternalValidation` only
   when the PAL-built chain has `ChainElements.Count > 1`, which does not hold for server-side
   client-certificate validation on OpenSSL. `GetRemoteCertificates()` reads the same field, so
   it is empty too and there is **no Kestrel-side workaround**.

   Impact: an application inspecting `ExtraStore` sees nothing, and a server that relies on
   peer-supplied intermediates rather than locally installed ones cannot build the chain.
   `ServerCertificateChainInExtraStore` no longer fails, but only because it configures
   `OnAuthenticate` and therefore now runs on `SslStream`.
2. **Proposal.** Expose the cipher-suite decomposition. `TlsSession` gives
   `NegotiatedCipherSuite` but not the legacy algorithm/strength values `ITlsHandshakeFeature`
   surfaces, so every consumer replacing `SslStream` must duplicate the runtime's internal
   table. Sharing the existing generated table through the runtime↔aspnetcore source-sharing
   contract would avoid new public API. The DirectTLS effort will hit this too.

---

## TL;DR

* **CPU per request drops 7–13%**, consistently across core counts and both platforms —
  measured exactly at **−13.7%** locally (`/proc` accounting, median of 3) and derived from
  the lab runs at −6.5% to −13% on Linux and −7.4% to −9.2% on Windows. This holds even
  where throughput is capped, and matches the earlier `TlsPoc.ServerCost` −10% round-trip
  figure. The exception is Windows at 56 cores, where the saving disappears (−1.3%).
* **The gain is +8–14% from 4 to 28 cores on both platforms.** Only the whole-machine
  (56-core) configuration is different, and not because of TLS: Linux does not scale past
  ~28 cores on this workload (56 cores is slower than 28 for *both* layers), and Windows
  keeps scaling but runs into some other limit. See "How core count affects the margin".
* **Official benchmarks, whole machine (the tracked configuration):** `plaintext https`
  **+6.1%** / `json https` **+8.1%** on Linux; **+1.9%** / **−0.5%** on Windows. These are
  the most conservative numbers in this document, for the reason above.
* On the same standard scenario with the server core-constrained to 8 cores (medians of 3
  runs): **Linux +8.8%**, **Windows +8.3%**. The Linux point is noisy (+5 to +11% across
  runs); Windows is stable to 0.4%.
* On the custom scenarios here (bombardier, 256 connections, 13-byte response):
  **Linux +8–9%**, **Windows +13–14%** at 2/4/8 cores. These predate the `NeedMoreData`
  fix and were not re-run; the standard-scenario sweep above supersedes them.
* **Linux used to lose 30–57% (requests/sec) because of a bug in this adapter, not in the
  runtime.** `TlsBufferSession` buffers ciphertext internally. When a client's first
  request arrived coalesced with its final handshake flight, those bytes were consumed
  into the session during the handshake, and this reader then awaited the transport
  instead of asking the session. The connection hung until the client timed out and
  reconnected, producing a handshake storm that burned the "missing" CPU.
* **The API was used incorrectly; it is not missing anything.**
  `TlsOperationStatus.NeedMoreData` is the session's explicit "fetch more bytes from the
  peer" signal, and the contract is to keep reading from the session until it returns
  that. This reader instead stopped when the *transport* buffer looked empty, which is a
  different condition, so it went to sleep while the session was still holding a record.
  `TlsPipeReader.TryDrainSession()` now loops on the status as intended.
* The old "bimodal / 50–60% of SslStream / intermittent 0 rps" observations were all this
  defect, amplified by WSL and shared-VM measurement error.
* Windows was previously measured at +6–10% *before* this fix existed; the same hang
  affected Windows, which is why the numbers are now higher.

---

## Results and provenance

All numbers below are **crank**, run against the ASP.NET perf lab with a separate load
generator, after the buffered-input fix. Each point is 2 runs per mode; medians shown.
Spread within a point was under 1.5% on Windows and under 2% on Linux.

### Head-to-head: scenarios `sslstream` vs `tlssession`

**Test:** `crank/tlspoc.benchmarks.yml`, scenarios `sslstream` and `tlssession`, profiles
`aspnet-gold-lin-relay` / `aspnet-gold-win-relay`, core count set with
`--application.cpuSet`. Load: bombardier, 256 connections, HTTP/1.1 keep-alive, 15 s warmup
+ 15 s duration, 13-byte response body, ECDSA P-256 certificate, TLS 1.3.
**Units:** requests/sec (median of 2 runs per point); Δ is tlssession relative to sslstream.

> **Measured on the pre-`NeedMoreData` build.** These were not re-run after that fix, so
> treat them as historical. The standard-scenario core sweep above is the current,
> better-replicated measurement, and it puts Windows at +8–10% rather than +13–14%. The
> difference is most likely build and load generator (bombardier here, wrk there) rather
> than anything about the platforms.

| server cores | Linux sslstream (req/s) | Linux tlssession (req/s) | **Linux Δ** | Windows sslstream (req/s) | Windows tlssession (req/s) | **Windows Δ** |
|---|---|---|---|---|---|---|
| 2 | 112,174 | 122,749 | **+9.4%** | 96,328 | 108,608 | **+12.7%** |
| 4 | 199,586 | 215,574 | **+8.0%** | 176,698 | 201,447 | **+14.0%** |
| 8 | 329,065 | 356,752 | **+8.4%** | 317,537 | 359,990 | **+13.4%** |
| 56 (whole machine) | 727,236 | 728,515 | +0.2% | not measured | | |

Zero bad responses in every run. Latency (p99, ms) and CPU (cores%, where 100% = one core)
also favour `tlssession`: Windows 8 cores **p99 1.31 ms vs 1.56 ms** at **741% vs 764%**
CPU; Linux 4 cores **p99 3.00 ms vs 3.15 ms** at **386% vs 396%**.

**The whole-machine result is parity, not a win.** At 56 cores both scenarios land in a
noisy 720k–800k req/s band (three iterations each, medians 727k vs 729k), so something
other than the TLS layer limits it there. Constrain the server and the win appears
consistently. An earlier single 56-core run showing +14.4% was inside that noise band and
should not be quoted.

### CPU per request — the efficiency result

Throughput is capped by whatever the bottleneck happens to be, but CPU per request is a
property of the work itself, so it holds up where the rps numbers flatten.

**Exact measurement**, local 6-core box, server pinned to 2 physical cores, bombardier 256
connections, CPU taken from `/proc/<pid>/stat` (utime+stime) across the measured window:

| iteration | sslstream | tlssession | reduction |
|---|---|---|---|
| 1 | 36.61 µs/req | 32.04 µs/req | −12.5% |
| 2 | 33.80 µs/req | 28.71 µs/req | −15.1% |
| 3 | 33.52 µs/req | 29.16 µs/req | −13.0% |
| **median** | **33.80** | **29.16** | **−13.7%** |

**Derived from the lab runs** as `(cores% / 100) × 10⁶ / rps`, on the standard
`plaintext https` and `json https` scenarios:

| run | Linux ssl | Linux tls | Linux Δ | Windows ssl | Windows tls | Windows Δ |
|---|---|---|---|---|---|---|
| plaintext, 4 cores | 5.02 | 4.37 | **−13.0%** | 6.72 | 6.14 | **−8.6%** |
| plaintext, 8 cores | 5.58 | 5.22 | **−6.5%** | 6.98 | 6.46 | **−7.4%** |
| plaintext, 16 cores | 7.09 | 6.40 | **−9.7%** | 7.53 | 6.91 | **−8.2%** |
| plaintext, 28 cores | 8.12 | 7.38 | **−9.1%** | 8.28 | 7.52 | **−9.2%** |
| plaintext, 56 cores | 16.60 | 15.50 | **−6.6%** | 11.36 | 11.21 | −1.3% |
| json, 56 cores | 52.27 | 46.94 | **−10.2%** | 44.26 | 43.44 | −1.9% |

(µs of CPU per request. Caveat: crank reports *max* cores usage, which is paired here with
*mean* rps, so the absolute values are inflated. The ratio between the two layers is the
meaningful part, since both are measured identically.)

**So the TLS layer costs roughly 7–13% less CPU per request**, consistently across core
counts and both platforms — which matches the earlier `TlsPoc.ServerCost` round-trip figure
of −10% measured in cycles.

The exception is Windows at 56 cores, where the CPU saving disappears too (−1.3% / −1.9%),
not just the throughput gain. Whatever dominates at that scale is swamping the record-layer
saving rather than merely capping it. On Linux at 56 cores the CPU saving survives (−6.6%
plaintext, −10.2% json) even though throughput is limited by the machine's scaling ceiling.

### How core count affects the margin

`plaintext https`, both platforms, `--application.cpuSet`. The 8- and 56-core points are
medians of 3 and 2 runs; 4, 16 and 28 are single runs, so read the *shape* rather than any
individual figure.

| server cores | Linux ssl | Linux tls | **Linux Δ** | Windows ssl | Windows tls | **Windows Δ** |
|---|---|---|---|---|---|---|
| 4 | 792,475 | 905,827 | **+14.3%** | 588,050 | 647,089 | **+10.0%** |
| 8 | 1,422,683 | 1,547,697 | **+8.8%** | 1,106,796 | 1,198,127 | **+8.3%** |
| 16 | 2,198,161 | 2,424,305 | **+10.3%** | 2,014,495 | 2,217,010 | **+10.1%** |
| 28 | 3,332,141 | 3,615,749 | **+8.5%** | 3,238,685 | 3,521,823 | **+8.7%** |
| 56 (whole machine) | 3,252,116 | 3,449,864 | **+6.1%** | 4,587,248 | 4,675,260 | **+1.9%** |

**The gain is a steady +8–14% from 4 to 28 cores on both platforms.** Only the
whole-machine point is different, and it is different for a reason that has nothing to do
with TLS:

* **Linux does not scale past ~28 cores on this workload.** 56 cores is *slower* than 28 for
  both layers (`sslstream` 3,252,116 vs 3,332,141; `tlssession` 3,449,864 vs 3,615,749).
  Once the machine is past its scaling peak, both layers are limited by the same thing and
  the TLS difference is squeezed.
* **Windows does keep scaling** (3.24M at 28 cores to 4.59M at 56), but the margin collapses
  to +1.9% there, so something else becomes the constraint at that rate.

Neither side is starved: at the whole machine the application uses about 5,000–5,400% of a
possible 5,600% CPU, and the load generator sits at 51–66%. On Windows `json` at 56 cores
`tlssession` actually uses *less* CPU than `sslstream` (4,852% vs 4,972%) for slightly
fewer requests - it is more efficient per request but cannot convert that into throughput.

So "Windows shows no benefit" is only true of the 56-core configuration. Windows gains
+8–10% everywhere below that, in line with Linux.

### Official benchmarks (whole machine — the tracked configuration)

These are the standard scenarios from aspnet/Benchmarks, run with no `cpuSet`, which is how
the tracked numbers are produced. Our server is substituted into the `application` job via
`--application.source.localFolder` and run once per `TLS_MODE`; the scenario definition,
load job and parameters are theirs.

**Units:** requests/sec, median of 2 runs. Zero bad responses in every run.

| scenario | platform | sslstream | tlssession | Δ |
|---|---|---|---|---|
| `plaintext https` | Linux | 3,252,116 | 3,449,864 | **+6.1%** |
| `plaintext https` | Windows | 4,587,248 | 4,675,260 | **+1.9%** |
| `json https` | Linux | 980,383 | 1,059,690 | **+8.1%** |
| `json https` | Windows | 1,127,318 | 1,121,504 | **−0.5%** |

Run-to-run spread within each point was under 1.2%, and on the Windows `json` point
`tlssession` was lower in both iterations, so the small regression there is not noise.

**Read the whole-machine row together with the core sweep above.** Windows gains +8–10%
at 4, 8, 16 and 28 cores and only flattens on the whole machine, where Linux stops scaling
outright and Windows meets some other limit. Quoting the 56-core row on its own understates
the change; quoting only the constrained rows overstates what the tracked dashboard will
show. Both belong together.

`json https` is the more representative of the two: `plaintext https` drives wrk with
`pipeline: 16`, which amortises per-request overhead and is not typical traffic.

### Standard scenario: `plaintext https` from aspnet/Benchmarks

The scenario definition does not care which binary backs the `application` job, so the
standard scenario can be run twice with this PoC's server substituted in, once per TLS
layer. This keeps the published load methodology (wrk, `pipeline: 16`, plaintext preset
headers, `/plaintext`) and only varies the TLS implementation.

### Core-constrained runs of `plaintext https` (supplementary)

Same substitution, with `--application.cpuSet 0-7`. These are supplementary to the
whole-machine numbers above, which are the tracked configuration.

**Units:** requests/sec, median of 3 runs on the current build.

| platform | server cores | sslstream (req/s) | tlssession (req/s) | Δ |
|---|---|---|---|---|
| Linux | 8 | 1,422,683 | 1,547,697 | **+8.8%** |
| Windows | 8 | 1,106,796 | 1,198,127 | **+8.3%** |

**The Linux point is noisy — treat single runs of it as unusable.** Across three identical
iterations `tlssession` spanned 1,491,228–1,574,370 (5.6%) while `sslstream` spanned only
2.3%, giving per-run deltas of +7.0%, +10.7% and +8.5%; a fourth run gave +5.5%. So the
honest range there is roughly +5 to +11%. Windows was far more stable: 0.4% spread on
`tlssession`, per-run deltas of +8.0%, +8.8% and +7.2%.

Earlier single runs of this configuration measured +10.6% (Linux) and +10.8% (Windows).
Both were optimistic samples, and the Linux one briefly looked like a regression when the
current build first measured +5.5%. Neither was real: they sit inside the same spread.
Anything quoted from this configuration needs at least three runs. CPU was level between
the layers (Linux 782% vs 777%).

Do not quote latency from this scenario either: wrk with pipelining reports p99 as `0.00`
in several runs, so only the request rate is meaningful.

```bash
crank --config https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/plaintext.benchmarks.yml \
      --scenario https --profile aspnet-gold-lin-relay --relay \
      --application.source.localFolder crank/app \
      --application.source.project src/TlsPoc.CrankServer/TlsPoc.CrankServer.csproj \
      --application.framework net11.0 \
      --application.environmentVariables SERVER_BIND=any \
      --application.environmentVariables TLS_MODE=tlssession
```

Three things that cost time here:

* **Do not also pass `aspnet.profiles.yml`.** `plaintext.benchmarks.yml` already imports
  it; passing it again duplicates the profile's endpoint list, so crank starts *two*
  application jobs on the same machine and the second fails with
  "Failed to bind to address https://[::]:5000: address already in use".
* That job nests the project inside its source, so the override is
  `--application.source.project`, not `--application.project`.
* The server must answer the scenario's path, hence the `/plaintext` endpoint in
  `TlsPoc.CrankServer` returning `Hello, World!` as `text/plain`.

### Memory — per-request unchanged; per-connection not resolvable with this instrument

**Per-request allocation is unchanged.** From crank counters on the standard `plaintext
https` scenario, Linux 8 cores, `--application.options.collectCounters true`:

| metric | sslstream | tlssession |
|---|---|---|
| requests/sec | 1,397,158 | 1,555,436 |
| Max allocation rate (B/sec) | 672,552,256 | 749,783,784 |
| **derived bytes/request** | **481** | **482** |

That is expected, and reinforced by the fact that `SslStream` is itself implemented on
`TlsBufferSession` (`SslStream.TlsSessionWedge.cs`): both paths drive the same TLS engine,
so only the buffering around it differs.

**Per-connection footprint could not be measured reliably.** Resident set size was sampled
with N connections held open, minus idle baseline. Repeating the 5,000-connection point
three times on identical code gave deltas of −6.5 KB, +3.9 KB and +13.9 KB per connection,
with `sslstream` alone ranging 83,335–94,297 B/conn. The instrument cannot resolve a
difference of this size, so **no per-connection memory claim should be made in either
direction**. An earlier version of this document reported a 9–27 KB/connection regression;
that was derived from one sample per point and does not survive repetition.

Measuring this properly needs something that separates live per-connection state from GC
slack and `ArrayPool` retention — a heap snapshot with the connections open, or an
in-process counter, rather than RSS.

**What was changed anyway.** The reader now returns its plaintext array to the pool when
nothing is buffered (in `AdvanceTo`, once `_start >= _end`), and the writer returns its
staging array after each flush. Previously both were held for the whole connection
lifetime, up to `MaxPlaintextRecord` (16 KB) each, so a keep-alive connection sitting idle
between requests retained them for nothing. This is a first-principles improvement rather
than a measured one; throughput was unaffected (62,119 and 65,610 req/s on the local
256-connection test, against 59,340 and 67,232 before, i.e. inside the same spread).

`_scratch`, rented at `MaxCipherRecord` (16.9 KB) on the first read that needs
linearising, is still held for the connection's lifetime.

### Response size sweep — where the win comes from

**Test:** same scenarios, `aspnet-gold-lin-relay`, `--application.cpuSet 0-7` (8 cores),
`--variable responseSize=N`. Load: bombardier, 256 connections, keep-alive.
**Units:** requests/sec; throughput in MB/s as reported by the load client.

| response body | sslstream (req/s) | tlssession (req/s) | Δ | tlssession (MB/s) |
|---|---|---|---|---|
| 13 B (default) | 325,762 | 340,638 | **+4.6%** | 68 |
| 1 KB | 344,209 | 367,995 | **+6.9%** | 414 |
| 16 KB | 203,619 | 224,445 | **+10.2%** | 3,548 |
| 100 KB | 45,411 | 45,471 | +0.1% | 4,456 |

The gain peaks at 16 KB — one maximum-size TLS record per response, where avoiding
`SslStream`'s two buffer copies saves the most per operation.

The 100 KB row is **not** evidence that the win disappears in bulk transfer: both layers
land on 4,456 MB/s, which is about 35.6 Gbps, i.e. the network link. The benchmark cannot
discriminate at that size; it would need a faster link or a loopback setup to say anything.

### Handshake cost — no trustworthy number yet

The `*-churn` scenarios are **not** currently a valid handshake benchmark and their numbers
should not be quoted. Three separate attempts were all confounded:

1. With TLS resumption on (the original default), `openssl s_client -reconnect` shows every
   reconnect is resumed. A resumed handshake does no signature, which is why swapping
   RSA-2048 for ECDSA P-256 moved `sslstream` by only 3.8% — the measurement never touched
   handshake cost. `tls-handshakes-kestrel` in aspnet/Benchmarks disables resumption for
   exactly this reason; this config had simply omitted it.
2. With resumption disabled (`TLS_RESUME=0`, now wired up), the lab produced internally
   inconsistent results — `sslstream` at 16 rps with 6.16 ms mean latency, and ECDSA
   slower than RSA. That is a broken run, not a slow one, and is unexplained.
3. Locally, `openssl s_time -new` is a single sequential client, so it is round-trip bound
   and mixes in client-side verification, where the algorithm costs run the *opposite* way
   to the server's. It reports RSA-2048 as faster than ECDSA P-256, which is the giveaway.

The only defensible handshake figure remains `TlsPoc.ServerCost` (−23% server CPU cycles),
because it measures server-only CPU directly rather than inferring it from a rate. Note it
was taken on Windows with an ECDSA P-256 certificate.

Handshake rate is not the interesting axis for this change anyway: handshake cost is
dominated by the certificate signature, so a per-operation saving in the record layer is
small next to it. The throughput scenarios are where the change shows up. If a handshake
rate is ever needed, the standard `tls-handshakes-kestrel` scenario in aspnet/Benchmarks is
the reference (it disables resumption, which is the part this config originally missed).

### Other measurements (pre-fix, still valid)

| Measurement | Result | Source |
|---|---|---|
| Handshake CPU | **−23%** (602 -> 465 kcycles) | `TlsPoc.ServerCost` (QueryThreadCycleTime) |
| Round-trip CPU | **−10%** (151.8 -> 136.4 kcycles) | `TlsPoc.ServerCost` |
| Allocation per connection | **−1.78 KB** (server-attributable); resident per-connection memory could not be measured reliably, see "Memory" | BenchmarkDotNet pairing matrix |

### What the fix changed on Linux

Pre-fix lab numbers, for the record — the PoC was losing badly because connections hung,
not because it was slow. **Test:** scenarios `sslstream` / `tlssession`,
`aspnet-gold-lin-relay`, `--application.cpuSet`, bombardier 256 connections, single runs.
**Units:** requests/sec.

| server cores | sslstream (req/s) | tlssession before (req/s) | tlssession after (req/s) |
|---|---|---|---|
| 2 | 113,873 | 65,147 | 122,948 |
| 4 | 196,147 | 100,268 | 213,323 |
| 8 | 324,915 | 228,923 | 350,046 |
| 16 | 541,379 | 250,232 | 620,742 |


## The one fix that came out of the investigation

`OutputSpanHint` was 4096, exactly Kestrel's output pipe segment size, so
`GetSpan(4096)` could never be served from a partly-used segment and started a fresh
one on every write. Each response fragmented across segments, turning a single-buffer
`sendto` into a vectored `sendmsg`.

`GetOutputSpan(hint)` now takes the remainder of the current segment when it is at
least `MinRecordSpan` (1 KB), escalating to a full record only if the session makes no
progress (which keeps the retry loops finite). Verified: `sendmsg` 19,764 -> 0.
Platform-independent; appears to be worth roughly 5 points on Windows.

---

## The connection hang, and how it was found

### Root cause — a bug in this adapter

`TlsBufferSession` buffers ciphertext **inside the session**. If the client's first
request arrives in the same TCP segment as its final handshake records - which is normal
for TLS 1.3 and gets more likely under load - the session consumes the whole segment
during the handshake and holds the request internally.

`TlsSessionDuplexPipe`'s reader then saw an empty transport buffer and awaited
`_transport.Input.ReadAsync`. Nothing more was coming: the bytes had already been read
off the socket and were sitting in the session. The server waited for a request it
already had, the client waited for a response, and the connection hung until the
client's timeout (10 s max latency in every single lab run, at every core count).

This is the adapter's mistake, not a runtime defect, and the API is not missing anything.
`TlsOperationStatus.NeedMoreData` means exactly "the session needs more data from the peer
to make progress", and the contract is to keep reading from the session until it says so.
A record may be whole or partial and may or may not yield output, which is precisely why
the session, rather than the transport buffer, has to be the authority. This reader used
"the transport buffer is empty" as its stop condition instead, so it parked while the
session still held a complete record.

### The fix

`TlsPipeReader.TryDrainSession()` reads from the session until it returns
`NeedMoreData`, and the reader only touches the transport once it sees that status. It
runs on the slow path only, so the hot path is unchanged. Post-fix, the same local
bombardier run gives 67,232 req/s with zero errors and no connection churn.

### Why it cost so much throughput

A hung connection was killed by the client and replaced, so the server paid for a fresh
handshake instead of serving requests.

**Test:** local (not lab) - `TLS_MODE=tlssession`, server pinned to 2 physical cores,
bombardier `-c 256 -d 30s -t 2s --fasthttp`, 13-byte response.

| metric | before fix | after fix |
|---|---|---|
| throughput (requests/sec) | 53,434 | **62,695** |
| max latency (ms) | 10,370 | **206** |
| latency stddev (ms) | 194 | **2.48** |
| client timeouts (count) | 498 | **0** |
| TCP connections created (256 configured) | 3,062 | **258** |
| transport reads per connection | 1.0 | **7,338** |

That handshake storm is exactly the "uses less CPU but cannot fill the machine" symptom:
stalled connections generate no work, so the thread pool drained and parked.

### How it was isolated (useful next time)

1. Bad responses and a 10 s max latency reproduced in the lab, not just locally, which
   killed the "WSL/shared-VM artefact" theory.
2. `TLS_MODE=sslpipe` (SslStream in the *same* middleware and pipe swap) was clean:
   0 errors, 223 ms max. That exonerated the middleware and the transport swap.
3. Raising Kestrel's log level to Debug showed its slowest request was **56 ms** while
   the client saw 10 s - so the lost time was never inside request processing.
4. Temporary instrumentation in the adapter (since removed) showed connections closing
   after a single read following a clean handshake exit
   (`complete=True leftover=0 pendingOut=False`), and stalled sockets had `Recv-Q=0`.
5. The giveaway was `consumed=24 produced=62`: 62 bytes of plaintext out of a 24-byte
   input is only possible if the session was already holding ciphertext. 62 bytes is a
   bombardier GET, which as a TLS record is 84 bytes - the `Recv-Q=84` seen earlier.

A red herring worth recording: server sockets sitting at `Recv-Q=84` looked like a
smoking gun until the `SslStream` control showed the same thing. It is ordinary
backpressure at 2 cores. Always run the control.

### Still open

At 28 cores the post-fix run came out 5.6% behind; every other point is 8–15% ahead.
Given ~±10% run-to-run drift at high core counts this is probably noise, but it has not
been replicated.


## Measurement traps (each of these produced a wrong conclusion here)

1. **`taskset -c 0,1` is ONE physical core.** cpu0/cpu1 are SMT siblings
   (`/sys/devices/system/cpu/cpu0/topology/thread_siblings_list` = `0-1`). Two real
   cores is `taskset -c 0,2`. Every "2 core" number taken before this was found is
   wrong. Check topology first.
2. **`dotnet-counters` CSV reports Rates, not cumulative values** for
   `work_item.count`, `lock_contentions`, `gc.collections`, `gc.heap.total_allocated`.
   Taking last-minus-first gives ~0. Use the mean of the Rate column / rps.
3. **WSL exaggerates this gap ~2.5x** (showed 5x where real hardware shows ~2x) and has
   huge variance. Do not draw conclusions from WSL.
4. **Read the `self` column, not `inclusive`**, before blaming a frame. A wrapper at
   27.55% inclusive / 0.08% self is not a bottleneck.
5. **Replicate before quoting a delta.** Single samples here have been off by 10 points.
6. Perf profiling of .NET on Linux needs `DOTNET_ReadyToRun=0` (R2R code is not in the
   perf map), `DOTNET_PerfMapEnabled=1`, `DOTNET_EnableWriteXorExecute=0`, and
   `perf report` must run **as the user owning `/tmp/perf-<pid>.map`**, in the **same
   script run** (a later `rm -f /tmp/perf-*.map` destroys resolution).

---

## Why this uses a custom crank config

The benchmarks here are custom (`crank/tlspoc.benchmarks.yml`), not scenarios from
aspnet/Benchmarks. That is deliberate and worth stating up front, because it is the first
thing a reviewer will question.

Every standard HTTPS scenario hardcodes `listenOptions.UseHttps(...)`, so the app behind it
can only ever construct `SslStream`. Comparing `SslStream` against the sans-IO layer needs
both reachable in the *same* application, over the same transport, cert and load - which is
what `TLS_MODE` does here (`sslstream` | `sslpipe` | `tlssession`). `sslpipe` exists purely
as a control: it runs `SslStream` through the identical custom middleware and `IDuplexPipe`
swap, so any difference it shows is the harness rather than the TLS layer.

That does **not** mean the standard scenarios are unusable. A crank scenario does not care
which binary backs the `application` job, so the standard `plaintext https` scenario can be
run twice with this server substituted in via `--application.source.localFolder`, once per
`TLS_MODE` - see "Standard scenario" above. That is the more externally meaningful
measurement, because the load configuration is the published one; the custom scenarios add
the `sslpipe` control and the response-size and certificate knobs that the standard one has
no way to express.

So: read these numbers as an A/B between two TLS layers under identical conditions, not as
figures comparable to any published benchmark result.

## A second adapter bug: `PipeWriter.UnflushedBytes`

`TlsPipeWriter` did not override `CanGetUnflushedBytes` / `UnflushedBytes`, and the base
`PipeWriter` implementation throws `NotSupportedException`. `System.Text.Json` queries it to
decide when to flush, so **any `WriteAsJsonAsync` response returned HTTP 500** on this TLS
layer, while the same endpoint worked under `sslstream`:

```
System.NotSupportedException: UnflushedBytes is not supported.
   at System.IO.Pipelines.PipeWriter.get_UnflushedBytes()
   at Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http.Http1OutputProducer.get_UnflushedBytes()
   at System.Text.Json.Serialization.JsonConverter.ShouldFlush(...)
```

The writer now tracks bytes written since the last flush and reports them. This was found
only because the standard `json` scenario needed a `/json` endpoint - every benchmark up to
that point returned plaintext, so nothing ever asked the writer for unflushed bytes. It is
a good argument for running a real application against this layer rather than just
benchmark endpoints, and the same class of gap may exist for other `PipeWriter` /
`PipeReader` members that Kestrel or middleware can reach.

## Correctness verification

`TlsPoc.Probe` covers the session directly (TLS 1.3, SNI, ALPN h2, 512 KiB both ways).
Beyond that, both TLS layers were compared end-to-end through Kestrel and produce
byte-identical results:

| check | result |
|---|---|
| `/plaintext` | identical |
| `/json` (`WriteAsJsonAsync`) | identical |
| `/echo` — 5 MB POST body, FNV hash of the received bytes | **identical hash**, `5000000:924540297` |
| `/chunked` — 64 × 1 KB writes, no Content-Length | identical length, `Transfer-Encoding: chunked` |
| server-side exceptions | none in either mode |

The 5 MB upload matters because it drives the reader across many reads and
`AdvanceTo` cycles, and the chunked response drives the writer with no
Content-Length. Those were the paths most likely to hide bookkeeping bugs.

`UnflushedBytes` is the only member of `PipeReader`/`PipeWriter` whose base
implementation throws, so there is no second gap of that kind. The remaining
surface (`CopyToAsync`, `ReadAtLeastAsync`, `AsStream`, `WriteAsync`) is built on the
members this adapter overrides.

**Not covered by the prototype harness:** the endpoint is configured `HttpProtocols.Http1`
and advertises only `http/1.1` via ALPN, so HTTP/2 is never exercised *here*. Client
certificates and renegotiation are likewise untested through this harness.

**All three are now covered on the integration branch** by the existing Kestrel suites run
with `-p:EnableSansIoTls=true`: HTTP/2 including the full h2spec suite (447 of 448 Interop
tests), client certificates, and renegotiation. See "State of that branch" above.

## Repo layout

```
src/TlsPoc.Core/TlsSessionDuplexPipe.cs   the PoC adapter (pull-based reader, inline encrypt)
src/TlsPoc.Core/SslStreamDuplexPipe.cs    baseline replicating Kestrel's SslDuplexPipe
src/TlsPoc.CrankServer/                   real Kestrel, TLS layer switchable (see env vars)
src/TlsPoc.LoadClient/                    load driver: <url> <connections> <seconds>
src/TlsPoc.Probe/                         correctness: TLS 1.3, SNI, ALPN h2, 512 KiB both ways
src/TlsPoc.ServerCost/                    cycle-accurate server-only CPU (QueryThreadCycleTime)
src/TlsPoc.AllocProbe/                    allocation probe
bench/TlsPoc.Benchmarks/                  BenchmarkDotNet suites
crank/tlspoc.benchmarks.yml               crank scenarios for the perf lab
```

### CrankServer environment variables

| Variable | Meaning |
|---|---|
| `TLS_MODE` | `sslstream` (Kestrel `UseHttps`), `sslpipe` (SslStream in our middleware), `tlssession` (the PoC) |
| `SERVER_PORT` | listen port, default 5000 |
| `SERVER_BIND` | `any` or `localhost` (default; avoids Windows firewall prompts) |
| `LOG` | `1` keeps Warning-level logging |
| `RESPONSE_SIZE` | response body size in bytes; `0` (default) keeps `Hello, World!` |
| `CERT_ALG` | `ecdsap256` (default) or `rsa2048` |
| `TLS_RESUME` | `0` disables TLS session resumption; required for any handshake measurement |
| `TLS_CTX_SHARDS` | diagnostic: shard connections over N `TlsContext`s |
| `TLS_CTX_PER_CONN` | diagnostic: one `TlsContext` per connection |
| `TLS_IOQ` | sets `SocketTransportOptions.IOQueueCount` |
| `TLS_INLINE_SCHED` | `1` sets `UnsafePreferInlineScheduling` |

---

## Building and running

The repo pins .NET 11 RC2 in `global.json`. The SDK lives in `.dotnet/` (gitignored).

Windows:
```powershell
cd kestrel-tlscontext-poc
$env:DOTNET_ROOT = "$PWD\.dotnet"
.\.dotnet\dotnet.exe build src\TlsPoc.CrankServer -c Release
```
Acquire the SDK if `.dotnet/` is missing:
`dotnet-install.ps1 -Version 11.0.100-rc.2.26468.110 -InstallDir .dotnet`

Linux: `dotnet-install.sh --version 11.0.100-rc.2.26469.104 --install-dir ~/.dotnet`

A/B run:
```bash
TLS_MODE=tlssession SERVER_PORT=5099 SERVER_BIND=any LOG=1 taskset -c 0,2 \
  dotnet src/TlsPoc.CrankServer/bin/Release/net11.0/TlsPoc.CrankServer.dll &
dotnet src/TlsPoc.LoadClient/bin/Release/net11.0/TlsPoc.LoadClient.dll \
  https://localhost:5099/ 128 12
```

---

## An Azure VM is a workable Linux environment (deleted; recreate if useful)

The Linux measurements above were taken on a throwaway Azure VM, since the ASP.NET
perf lab needs VPN. **Those resources have been deleted.** Recreating one is cheap and
is a reasonable option if no physical Linux box is available — but see the caveat at
the end of this section.

```bash
az group create -n tlspoc-perf-rg -l westus2
az vm create -g tlspoc-perf-rg -n tlspoc-lin --image Ubuntu2404 \
  --size Standard_D16s_v5 --admin-username azureuser --generate-ssh-keys \
  --os-disk-size-gb 64 --public-ip-sku Standard --nsg-rule SSH
```

`Standard_D16s_v5` is 8 physical cores x 2 SMT. Provision with: .NET 11 RC2
(`dotnet-install.sh --version 11.0.100-rc.2.26469.104`), `strace`, `gdb`,
`linux-tools-azure` (real `perf`), `dotnet-symbol` for `libcoreclr.so.dbg`, and
`sysctl -w kernel.perf_event_paranoid=-1 kernel.kptr_restrict=0`.

**SSH did not work from the corp network** — outbound TCP was blocked on 22 *and* 443
(no proxy variables set; PowerShell's HTTPS goes via a system proxy, raw TCP does not).
Everything was driven through the agent channel instead:

```powershell
az vm run-command invoke -g tlspoc-perf-rg -n tlspoc-lin `
  --command-id RunShellScript --scripts "@script.sh" --query "value[0].message" -o tsv
```

run-command gotchas:
* its cwd (`/var/lib/waagent/run-command/download/N`) is deleted, so scripts must
  `cd $HOME/tlspoc` or `CreateSlimBuilder` throws "content root does not exist";
* never `bash -lc '<whole script>'` — the script text lands in the process command line
  and `pkill -f TlsPoc.CrankServer` then kills its own shell. Write the script to a file
  on the VM and run the file;
* source transfer works by inlining a base64 tarball in the script (76 KB -> 102 KB).

Teardown: `az group delete -n tlspoc-perf-rg --yes`.

**Caveat:** a single VM running both server and load client is what defeated this
investigation — identical configs ranged 12.5k–51.2k rps. If you use a VM, use two
(separate load generator) and pin the server to non-sibling CPUs.

---

## Next steps

1. Build a per-connection memory measurement that actually resolves. RSS sampling does
   not: the same 5,000-connection point varied by 20 KB/connection across identical runs.
   A heap snapshot taken with the connections open, or an in-process counter, would settle
   whether this adapter's remaining buffering (`_scratch`, still held for the connection
   lifetime) costs anything against `SslStream`.
2. Work out what limits the whole-machine (56-core) case, where both TLS layers land at
   the same ~727k rps. The win is consistent whenever the server is core-constrained, so
   the ceiling there is probably the load generator or the network, not Kestrel.
3. Re-run the churn scenarios (`sslstream-churn` / `tlssession-churn`) now that
   connection reuse works; they were measuring the bug.
4. Consider whether `_scratch` (16.9 KB, still held for the connection lifetime) is worth
   releasing too, once there is a measurement that can actually detect the difference.
5. Audit the rest of the `PipeReader` / `PipeWriter` surface for members Kestrel or
   middleware can reach. `UnflushedBytes` was missing and broke `WriteAsJsonAsync`; the
   base-class review since then found no other member that throws, but behaviour-only gaps
   would not show up that way.
6. Add a regression test for the coalesced case: a client that sends its first request in
   the same flight as its final handshake records must not stall.

## Platform support and rollout

`TlsSession` / `TlsBufferSession` ship on **Windows, Linux and macOS in .NET 11 RC2**. The
Android implementation is a pending PR targeting .NET 12. Reading the file layout in
dotnet/runtime is misleading - only `TlsContext.OpenSsl.cs` / `TlsSession.OpenSsl.cs` and a
`TlsSession.Stub.cs` stand out - but `SslStream.TlsSessionWedge.cs` routes `SslStream`'s own
hot path through `TlsSession` on Linux, FreeBSD and Windows, so the session is already in
production use on those platforms.

A rollout that **switches Windows and Linux to the sans-IO path and leaves the remaining
platforms on `SslStream`** would be a reasonable first step, and both platforms in that set
are measured here. **This is a proposal, not something implemented:** there is no platform
conditional anywhere in this prototype. `TLS_MODE` selects the layer explicitly, and both
platforms ran the identical `tlssession` code path in every measurement above.

Known gaps if this ever ships: the middleware bypasses `HttpsConnectionMiddleware`, so
Kestrel's TLS counters and `ITlsHandshakeFeature` are lost.

`dotnet/aspnetcore` main now pins SDK `11.0.100-rc.1`, so the sans-IO APIs are available
there and the earlier note in this document about a .NET 10 SDK blocking integration is out
of date.

---

## Reproducing the Linux measurements

The lab is reachable from outside the corp network through the Azure Relay profiles
(`aspnet-gold-lin-relay`), authenticated with `az login` — no VPN and no connection
string needed:

```bash
crank --config crank/tlspoc.benchmarks.yml \
      --config https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/aspnet.profiles.yml \
      --scenario tlssession --profile aspnet-gold-lin-relay --relay \
      --application.cpuSet "0-7"
```

`crank/app/` is a staged copy of the sources and is gitignored; recreate it before a run:

```bash
rm -rf crank/app && mkdir -p crank/app/src
rsync -a --exclude bin --exclude obj src/TlsPoc.CrankServer src/TlsPoc.Core crank/app/src/
cp Directory.Build.props global.json crank/app/
```

Gotchas:

* The job sets `SERVER_BIND: any`. Without it the server binds to loopback, every lab
  profile reports **100% bad responses**, and crank still prints a plausible-looking rps.
* `--application.cpuSet` needs cgroup tools on the agent. It works on the lab machines;
  on a dev box without `cgcreate` the job fails, so pin with `taskset` instead.
* SMT topology differs per machine — check
  `/sys/devices/system/cpu/cpu0/topology/thread_siblings_list` before assuming that
  `taskset -c 0,1` is one physical core or two. On the Xeon E5-1650 v4 the siblings are
  `(0,6) (1,7) ...`, so `0,1` really is two physical cores.
* `crank-agent` targets .NET 8; do not run it with `DOTNET_ROOT` pointed at the repo's
  .NET 11 SDK.

### If the stall ever comes back

The instrumentation used to find it was temporary and has been removed. What identified
it quickly: a per-connection watchdog reporting connections stuck in one state for more
than ~700 ms, counters for handshakes and per-connection reads, and the `consumed`/
`produced` values from `TlsBufferSession.Read`. `TLS_MODE=sslpipe` is the control that
separates the adapter from the middleware, and Kestrel at Debug level tells you whether
time is being lost inside request processing or before it.
