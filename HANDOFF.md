# Kestrel on .NET 11 sans-IO TLS — PoC status and handoff

PoC replacing Kestrel's `SslStream` TLS layer with the .NET 11 experimental sans-IO
primitives (`TlsContext` / `TlsBufferSession`, `SYSLIB5007`), to see whether skipping
the `Stream` shim is worth anything in CPU, memory or RPS.

Kestrel today:   transport pipe -> DuplexPipeStream -> SslStream -> StreamPipeReader/Writer -> app
This PoC:        transport pipe -> TlsSessionDuplexPipe (decrypt/encrypt inline) -> app

---

## TL;DR

* **Both platforms are a win.** On the **standard** `plaintext https` scenario from
  aspnet/Benchmarks, run twice with this server substituted into the application job:
  **Linux +10.6%**, **Windows +10.8%** at 8 cores (requests/sec, wrk, `pipeline: 16`),
  and +4.0% / +1.9% on the whole 56-core machine. On the custom scenarios here
  (`sslstream` vs `tlssession`, bombardier, 256 connections, 13-byte response):
  **Linux +8–9%**, **Windows +13–14%** at 2/4/8 cores, with lower p99 latency and equal
  or slightly lower CPU. Zero bad responses throughout.
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

### Standard scenario: `plaintext https` from aspnet/Benchmarks

The scenario definition does not care which binary backs the `application` job, so the
standard scenario can be run twice with this PoC's server substituted in, once per TLS
layer. This keeps the published load methodology (wrk, `pipeline: 16`, plaintext preset
headers, `/plaintext`) and only varies the TLS implementation.

**Test:** `plaintext.benchmarks.yml`, scenario `https`, unmodified; application job
replaced via `--application.source.localFolder`. **Units:** requests/sec.

| platform | server cores | sslstream (req/s) | tlssession (req/s) | Δ |
|---|---|---|---|---|
| Linux | 8 | 1,426,765 | 1,577,348 | **+10.6%** |
| Linux | 56 (whole machine) | 3,248,090 | 3,376,937 | **+4.0%** |
| Windows | 8 | 1,103,177 | 1,222,503 | **+10.8%** |
| Windows | 56 (whole machine) | 4,563,672 | 4,652,155 | **+1.9%** |

Zero bad responses in all runs. CPU was level between the two layers (Linux 8 cores 786%
vs 787%; Windows 8 cores 785% vs 777%). As with the custom scenarios, the margin is
largest when the server is core-constrained and shrinks on the whole machine, where
something other than the TLS layer limits throughput.

Do not quote latency from this scenario: wrk with pipelining reports p99 as `0.00` in
several of these runs, so only the request rate is meaningful here.

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

### Memory — no win, and a per-connection regression

Two separate questions, with different answers.

**Per-request allocation is unchanged.** From crank counters on the standard `plaintext
https` scenario, Linux 8 cores, `--application.options.collectCounters true`:

| metric | sslstream | tlssession |
|---|---|---|
| requests/sec | 1,397,158 | 1,555,436 |
| Max allocation rate (B/sec) | 672,552,256 | 749,783,784 |
| **derived bytes/request** | **481** | **482** |
| Max GC heap size (MB) | 52 | 31 |
| GC committed memory (MB) | 54 | 70 |
| Max time in GC (%) | 7 | 8 |

That is expected: this design removes *per-connection* objects, not per-request ones, so a
256-connection benchmark cannot show a memory difference. (Heap size vs committed memory
disagree in direction here, so neither should be quoted on its own.)

**Per-connection footprint is worse, not better — and it is this adapter's doing.**
`SslStream` is itself implemented on top of `TlsBufferSession` (`SslStream.TlsSessionWedge.cs`
routes its hot path through the session, on Linux, FreeBSD and Windows). So this is not two
TLS engines being compared: it is the same engine with different buffering wrapped around
it, which is also why per-request allocation comes out identical. Any memory difference is
therefore attributable to the wrapper, not to the sans-IO design.

Measured locally (6-core box, server GC, `TlsPoc.LoadClient` holding N connections, RSS
sampled 15 s in, minus idle baseline):

| open connections | sslstream (B/conn) | tlssession (B/conn) | delta |
|---|---|---|---|
| 1,000 | 125,243 | 151,994 | **+26.8 KB** |
| 5,000 | 96,932 | 113,715 | **+16.8 KB** |
| 10,000 | 81,867 | 90,792 | **+8.9 KB** |

The cause is that each connection holds pooled buffers for its whole lifetime:
`_plaintext` and `_staging` start at `InitialBufferSize` (4 KB) and grow towards
`MaxPlaintextRecord` (16 KB), and `_scratch` is rented at `MaxCipherRecord` (16.9 KB) the
first time a read has to be linearised. `ArrayPool` rentals are not *allocations* once the
pool is warm, which is why the BenchmarkDotNet pairing matrix reports a small allocation
win (−1.78 KB/connection) while resident memory goes the other way. Both are true; they
measure different things, and the resident figure is the one that matters at scale.

Caveats: RSS on a server-GC process includes heap slack that is not per-connection state,
and this is a single local box rather than the lab. The direction is consistent across all
three connection counts, so it should not be dismissed, but the absolute numbers are soft.

Since `SslStream` gets by without these buffers over the same session, they are a wrapper
design question rather than a cost of the approach - see next steps.

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
| Allocation per connection | **−1.78 KB** (server-attributable) — but see "Memory" above: resident memory per connection is *higher* | BenchmarkDotNet pairing matrix |

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

1. Reduce this adapter's per-connection buffering. `SslStream` is itself implemented on
   `TlsBufferSession` (see `SslStream.TlsSessionWedge.cs`), so the memory difference
   measured here is not inherent to the sans-IO approach - it is the extra `_plaintext`,
   `_staging` and `_scratch` buffers this adapter keeps on top of the session for each
   connection's lifetime. That is where the 9–27 KB/connection regression comes from, and
   removing or shrinking those buffers is the fix.
2. Work out what limits the whole-machine (56-core) case, where both TLS layers land at
   the same ~727k rps. The win is consistent whenever the server is core-constrained, so
   the ceiling there is probably the load generator or the network, not Kestrel.
3. Re-run the churn scenarios (`sslstream-churn` / `tlssession-churn`) now that
   connection reuse works; they were measuring the bug.
4. Tune per-connection buffer sizing (`InitialBufferSize`, `_scratch` at `MaxCipherRecord`).
   Resident memory per connection is currently 9–27 KB worse than `SslStream`; nothing here
   has been tuned for footprint.
5. Add a regression test for the coalesced case: a client that sends its first request in
   the same flight as its final handshake records must not stall.

## Platform support and rollout

`TlsSession` / `TlsBufferSession` ship on **Windows, Linux and macOS in .NET 11 RC2**. The
Android implementation is a pending PR targeting .NET 12. Reading the file layout in
dotnet/runtime is misleading - only `TlsContext.OpenSsl.cs` / `TlsSession.OpenSsl.cs` and a
`TlsSession.Stub.cs` stand out - but `SslStream.TlsSessionWedge.cs` routes `SslStream`'s own
hot path through `TlsSession` on Linux, FreeBSD and Windows, so the session is already in
production use on those platforms.

The suggested rollout is therefore to **switch Windows and Linux to the sans-IO path and
leave the remaining platforms on `SslStream`** until the rest lands. Both platforms in that
set are measured here.

Known gaps if this ever ships: the middleware bypasses `HttpsConnectionMiddleware`, so
Kestrel's TLS counters and `ITlsHandshakeFeature` are lost. Integration into
dotnet/aspnetcore is also blocked while that repo pins a .NET 10 SDK.

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
