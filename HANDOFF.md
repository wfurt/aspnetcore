# Kestrel on .NET 11 sans-IO TLS — PoC status and handoff

PoC replacing Kestrel's `SslStream` TLS layer with the .NET 11 experimental sans-IO
primitives (`TlsContext` / `TlsBufferSession`, `SYSLIB5007`), to see whether skipping
the `Stream` shim is worth anything in CPU, memory or RPS.

Kestrel today:   transport pipe -> DuplexPipeStream -> SslStream -> StreamPipeReader/Writer -> app
This PoC:        transport pipe -> TlsSessionDuplexPipe (decrypt/encrypt inline) -> app

---

## TL;DR

* **Both platforms are a win, crank-verified against the perf lab after the fix:**
  **Linux +8–9%** and **Windows +13–14%** at 2/4/8 cores, with better p99 latency, equal
  or slightly less CPU, and zero bad responses. On the whole 56-core machine it is
  parity — something other than TLS is the limit there.
* **Linux used to lose 30–57%, and that was a deadlock, not slowness.**
  `TlsBufferSession` buffers ciphertext internally. When the client's first request
  arrived coalesced with its final handshake flight, the request ended up inside the
  session while the reader blocked on the transport for bytes already consumed. The
  connection deadlocked until the client timed out, closed and reconnected — producing a
  handshake storm that burned the "missing" CPU.
* The fix is `TlsPipeReader.TryDrainSession()`: drain plaintext the session already holds
  before blocking on the transport. Slow path only.
* The old "bimodal / 50–60% of SslStream / intermittent 0 rps" observations were all this
  defect, amplified by WSL and shared-VM measurement error.
* Windows was previously measured at +6–10% *before* this fix existed; the deadlock hit
  Windows too, which is why the numbers are now higher.

---

## Results and provenance

All numbers below are **crank**, run against the ASP.NET perf lab with a separate load
generator, after the buffered-input fix. Each point is 2 runs per mode; medians shown.
Spread within a point was under 1.5% on Windows and under 2% on Linux.

### Head-to-head, `sslstream` vs `tlssession`

| cores | Linux ssl | Linux tls | **Linux** | Windows ssl | Windows tls | **Windows** |
|---|---|---|---|---|---|---|
| 2 | 112,174 | 122,749 | **+9.4%** | 96,328 | 108,608 | **+12.7%** |
| 4 | 199,586 | 215,574 | **+8.0%** | 176,698 | 201,447 | **+14.0%** |
| 8 | 329,065 | 356,752 | **+8.4%** | 317,537 | 359,990 | **+13.4%** |
| 56 (whole machine) | 727,236 | 728,515 | +0.2% | not measured | | |

Zero bad responses in every run. The PoC also wins on latency and uses equal or slightly
less CPU at every point — e.g. Windows 8 cores: p99 **1.31 ms vs 1.56 ms** at 741% vs
764% CPU; Linux 4 cores: p99 **3.00 ms vs 3.15 ms** at 386% vs 396%.

**The whole-machine result is parity, not a win.** At 56 cores both modes land in a noisy
720k–800k band (three iterations each, medians 727k vs 729k), so something other than the
TLS layer is the limit there. Constrain the server and the win appears consistently. An
earlier single 56-core run showing +14.4% was inside that noise band and should not be
quoted.

### Response size sweep — where the win comes from

`aspnet-gold-lin`, 8 cores, bombardier 256 connections, `--variable responseSize=N`:

| response | ssl rps | tls rps | Δ | tls throughput |
|---|---|---|---|---|
| 13 B (default) | 325,762 | 340,638 | **+4.6%** | 68 MB/s |
| 1 KB | 344,209 | 367,995 | **+6.9%** | 414 MB/s |
| 16 KB | 203,619 | 224,445 | **+10.2%** | 3,548 MB/s |
| 100 KB | 45,411 | 45,471 | +0.1% | 4,456 MB/s |

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

To get a publishable handshake rate, port this PoC's TLS layer into the standard app
(`src/BenchmarksApps/TLS/Kestrel` in aspnet/Benchmarks) and run the standard
`tls-handshakes-kestrel` scenario against it — crank can upload a modified local copy with
`--application.source.localFolder` plus `--application.project`. That keeps the published
baseline's methodology, including its resumption handling. The app targets net9.0 today and
would need retargeting to net11.0 for the sans-IO APIs.

### Other measurements (pre-fix, still valid)

| Measurement | Result | Source |
|---|---|---|
| Handshake CPU | **−23%** (602 -> 465 kcycles) | `TlsPoc.ServerCost` (QueryThreadCycleTime) |
| Round-trip CPU | **−10%** (151.8 -> 136.4 kcycles) | `TlsPoc.ServerCost` |
| Allocation per connection | **−1.78 KB** (server-attributable) | BenchmarkDotNet pairing matrix |

### What the fix changed on Linux

Pre-fix lab numbers, for the record — the PoC was losing badly because of the deadlock,
not because it was slow (single runs, `--application.cpuSet`):

| cores | ssl | tls before | tls after |
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

## Linux: the deadlock, and how it was found

### Root cause

`TlsBufferSession` buffers ciphertext **inside the session**. If the client's first
request arrives in the same TCP segment as its final handshake records - which is normal
for TLS 1.3 and gets more likely under load - the session consumes the whole segment
during the handshake and holds the request internally.

`TlsSessionDuplexPipe`'s reader then saw an empty transport buffer and awaited
`_transport.Input.ReadAsync`. Nothing more was coming: the bytes had already been read
off the socket and were sitting in the session. The server waited for a request it
already had, the client waited for a response, and the connection deadlocked until the
client's timeout (10 s max latency in every single lab run, at every core count).

The API makes this easy to get wrong: there is `DrainPendingOutput` for buffered output,
but **no equivalent for buffered input** and no way to ask whether the session is holding
any. Worth raising against the sans-IO API - `TlsBufferSession` is `[Experimental]`.

### The fix

`TlsPipeReader.TryDrainSession()` pulls plaintext the session already holds, by calling
`Read` with an empty source, and the reader now does that **before** blocking on the
transport. It only runs on the slow path, so the hot path is unchanged.

### Why it cost so much throughput

A deadlocked connection was killed by the client and replaced, so the server paid for a
fresh handshake instead of serving requests. At 256 connections on 2 cores:

| | before | after |
|---|---|---|
| rps | 53,434 | **62,695** |
| max latency | 10.37 s | **206 ms** |
| latency stddev | 194 ms | **2.48 ms** |
| timeouts | 498 | **0** |
| connections created (256 configured) | 3,062 | **258** |
| reads per connection | 1.0 | **7,338** |

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

1. Raise the buffered-input gap against the sans-IO TLS API: there is no way to ask
   whether `TlsBufferSession` is holding ciphertext, and no input counterpart to
   `DrainPendingOutput`. Any other consumer will hit the same deadlock, so this is worth
   filing while the API is still `[Experimental]`.
2. Work out what limits the whole-machine (56-core) case, where both TLS layers land at
   the same ~727k rps. The win is consistent whenever the server is core-constrained, so
   the ceiling there is probably the load generator or the network, not Kestrel.
3. Re-run the churn scenarios (`sslstream-churn` / `tlssession-churn`) now that
   connection reuse works; they were measuring the bug.
4. Add a regression test for the coalesced case: a client that sends its first request in
   the same flight as its final handshake records must not stall.

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
