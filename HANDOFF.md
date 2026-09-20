# Kestrel on .NET 11 sans-IO TLS — PoC status and handoff

PoC replacing Kestrel's `SslStream` TLS layer with the .NET 11 experimental sans-IO
primitives (`TlsContext` / `TlsBufferSession`, `SYSLIB5007`), to see whether skipping
the `Stream` shim is worth anything in CPU, memory or RPS.

Kestrel today:   transport pipe -> DuplexPipeStream -> SslStream -> StreamPipeReader/Writer -> app
This PoC:        transport pipe -> TlsSessionDuplexPipe (decrypt/encrypt inline) -> app

---

## TL;DR

* **Windows: a real win, independently verified.** +6–10% RPS in the ASP.NET perf lab
  via crank, across three sweeps at 2/4/8 cores, at equal CPU.
* **Linux: works and can reach full parity, but is unstable.** One run measured dead
  parity with `SslStream`; most runs land ~50–60% of it. Root cause of the slow state
  is **not** found. This is the open item.
* The design itself is not the problem: at 1 connection the PoC is the fastest of the
  three modes, and the middleware has been positively exonerated (see below).

---

## Results and provenance

Provenance matters — only the lab numbers are independent.

| Measurement | Result | Source |
|---|---|---|
| Windows lab, 2 / 4 / 8 cores | **+7.3/8.2/8.4%**, **+8.1/9.2/10.6%**, **+5.7/6.2/7.0%** | **crank**, `aspnet-gold-win`, 3 sweeps |
| Windows local, post-`sendmsg` fix, 4 / 8 cores | +14.5% / +15.6% | own harness, shared dev box |
| Handshake CPU | **−23%** (602 -> 465 kcycles) | `TlsPoc.ServerCost` (QueryThreadCycleTime) |
| Round-trip CPU | **−10%** (151.8 -> 136.4 kcycles) | `TlsPoc.ServerCost` |
| Allocation per connection | **−1.78 KB** (server-attributable) | BenchmarkDotNet pairing matrix |
| Linux (Azure VM), 2 physical cores, 128 conns | ssl 59.7k / 60.5k vs tls 36.2k / 29.1k | own harness |
| Linux, best observed run | **51,161 @ 36.3 us/req vs ssl 50,250 @ 37.2 us — parity** | own harness |

The local Windows post-fix numbers are NOT independently verified — the perf lab needs
VPN, which was unavailable. Between-round drift on the dev box is +5%..+19%, so the
defensible claim is "+6–10%, crank-verified".

---

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

## Linux: what is actually happening

Behaviour is **bimodal**, not uniformly slow. Eight identical runs at 128 connections:

* slow cluster (5 runs): 23.9k–27.1k rps, 62–70 us/req
* fast cluster (3 runs): 34.8k–38.3k rps, 41.7–46.5 us/req

The fast state uses **less** CPU and delivers **more** throughput. With correct
physical-core pinning the PoC runs at 50–60% of `SslStream` while using less CPU
(1.47 of 2 cores vs 1.85) — it cannot fill the machine. `ThreadNative_SpinWait`
(thread-pool spin-before-park) is 30–49% of samples, but it is a **symptom**: setting
the spin limit to 0 removes the frame and does not change throughput.

Occasionally a run returns **0 rps** outright. Seen in WSL and once on the VM. This is
a separate, real robustness bug and has not been investigated.

### Positively exonerated

`TLS_MODE=sslpipe` runs `SslStream` inside the *same* custom middleware and the same
`IDuplexPipe` swap. It matches Kestrel's `UseHttps` exactly (49,564 vs 49,955 @ 32
conns), so the middleware, the `connection.Transport` swap, the pipe indirection and
the harness are all fine. The defect is inside `TlsSessionDuplexPipe`.

### Ruled out as the cause (all measured, all negative)

Shared `TlsContext` (sharding over 2/8/32 contexts and per-connection contexts change
nothing — and separate contexts would kill TLS resume anyway); OpenSSL locking (108
sampled threads, zero `libssl` frames, zero mutex waits); handshake churn (32 ESTAB
sockets start and end); syscall counts (recvfrom 2.004 vs 2.013, sendto 1.002 vs 1.038
per request); allocation (1,877 vs 2,160 B/req); thread-pool hand-offs (2.51 vs 2.55
per request); managed lock contention (~0 both); thread-pool size; JIT tiering
(`TieredCompilation=0` made both worse); `IOQueueCount`;
`DOTNET_SYSTEM_NET_SOCKETS_THREAD_COUNT`; inline socket completions;
`UnsafePreferInlineScheduling`; the load client (2.0–2.6 cores used in both modes,
14 cores available).

### Best remaining lead

In the resolved inclusive tree, `Task.RunContinuations` (27.25%) and
`AwaitTaskContinuation.RunOrScheduleAction` (27.14%) are hot in the PoC and absent
from `SslStream`'s top frames. `RunOrScheduleAction` is the "cannot run inline, queue
it to the thread pool" path. Meanwhile `ThreadPoolWorkQueue.Dispatch` is 53% for the
PoC vs 90% for `SslStream`. Consistent with: same work-items/request but a drained
queue, park/wake churn, and spin.

The diff that was never completed: capture a fast-state and a slow-state profile
**back to back in one run** and diff them against each other rather than against
`SslStream`. The last attempt failed because no fast state occurred in that batch.

---

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

1. Get onto a **physical Linux box with a separate load generator**. Client contention
   and machine drift are what defeated the investigation on the shared Azure VM;
   identical configs there ranged 12.5k–51.2k rps.
2. Re-baseline the A/B with correct physical-core pinning before anything else.
3. Do the fast-vs-slow state diff in a single run.
4. Investigate the intermittent 0-rps failure — it may be the same bug.
5. Re-run the Windows lab sweep post-`sendmsg`-fix to confirm whether the win is now
   above +10%.

Known gaps if this ever ships: the middleware bypasses `HttpsConnectionMiddleware`, so
Kestrel's TLS counters and `ITlsHandshakeFeature` are lost. Integration into
dotnet/aspnetcore is also blocked while that repo pins a .NET 10 SDK.
