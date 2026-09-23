#!/usr/bin/env bash
#
# Reproduces the throughput numbers in HANDOFF.md.
#
# Runs a *standard* scenario from aspnet/Benchmarks (the scenario definition and its load
# configuration are theirs, untouched) and substitutes this repo's server into the
# application job, once per TLS layer. That is what makes an A/B possible: the standard
# apps hardcode UseHttps, so they can only ever exercise SslStream.
#
# usage: crank/run-benchmarks.sh [options]
#   -s plaintext|json   scenario          (default plaintext)
#   -p lin|win          lab machine       (default lin)
#   -c <cpuset>         e.g. 0-7          (default: whole machine)
#   -n <iterations>     repeats per mode  (default 1)
#   -o <dir>            output directory  (default /tmp/crankrun)
#
# Examples:
#   crank/run-benchmarks.sh                          # plaintext, Linux, whole machine
#   crank/run-benchmarks.sh -c 0-7 -n 3              # 8 cores, 3 iterations
#   crank/run-benchmarks.sh -s json -p win -n 2      # json, Windows, 2 iterations

set -uo pipefail

SCENARIO=plaintext
PLATFORM=lin
CPUSET=""
ITERS=1
OUT=/tmp/crankrun

while getopts "s:p:c:n:o:h" opt; do
  case $opt in
    s) SCENARIO=$OPTARG ;;
    p) PLATFORM=$OPTARG ;;
    c) CPUSET=$OPTARG ;;
    n) ITERS=$OPTARG ;;
    o) OUT=$OPTARG ;;
    h) sed -n '3,22p' "$0"; exit 0 ;;
    *) exit 1 ;;
  esac
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 1

# The repo default locale here is cs_CZ, which makes .NET's "N0" format use a space as the
# thousands separator and silently breaks every number parsed out of crank's output.
export LC_ALL=C LANG=C

# crank-agent and the crank controller target .NET 8. Pointing DOTNET_ROOT at this repo's
# .NET 11 SDK makes them fail to launch, so use the system install for the tool itself.
export DOTNET_ROOT=/usr/lib/dotnet
export PATH="$HOME/.dotnet/tools:/usr/lib/dotnet:$PATH"

case $PLATFORM in
  lin) PROFILE=aspnet-gold-lin-relay ;;
  win) PROFILE=aspnet-gold-win-relay ;;
  *)   echo "unknown platform '$PLATFORM' (use lin or win)"; exit 1 ;;
esac

CONFIG="https://raw.githubusercontent.com/aspnet/Benchmarks/main/scenarios/${SCENARIO}.benchmarks.yml"

# ---------------------------------------------------------------------------------------
# Preflight. Each of these has cost a failed run at least once.
# ---------------------------------------------------------------------------------------

command -v crank >/dev/null || {
  echo "crank not found. Install with:"
  echo "  dotnet tool install -g Microsoft.Crank.Controller --version '0.2.0-*'"
  exit 1
}

az account show >/dev/null 2>&1 || {
  echo "Not logged in to Azure. The lab is reached over the Azure Relay, which needs:"
  echo "  az login --use-device-code --allow-no-subscriptions"
  exit 1
}

# crank/app is gitignored, so it is absent on a fresh clone and stale after any source
# edit. Always rebuild it: it is only ~90 KB.
echo "staging crank/app from src/ ..."
rm -rf crank/app
mkdir -p crank/app/src
rsync -a --exclude bin --exclude obj src/TlsPoc.CrankServer src/TlsPoc.Core crank/app/src/
cp Directory.Build.props global.json crank/app/

mkdir -p "$OUT"
rm -f "$OUT/results"

echo "scenario=$SCENARIO profile=$PROFILE cores=${CPUSET:-all} iterations=$ITERS"
printf "%-6s %-11s %12s %8s %8s %8s\n" run mode req/s cores% p99ms bad

value() { grep -E "^\| $2 " "$1" | head -1 | sed 's/.*| *\([0-9,.]*\) *|.*/\1/' | tr -d ,; }

for i in $(seq "$ITERS"); do
  for mode in sslstream tlssession; do
    f="$OUT/$mode.$i.txt"

    # NOTE: do not also pass aspnet.profiles.yml. The scenario file already imports it, and
    # passing it again duplicates the profile's endpoint list, so crank starts *two*
    # application jobs on the same machine and the second dies with
    # "Failed to bind to address https://[::]:5000: address already in use".
    #
    # NOTE: the override is --application.source.project, not --application.project,
    # because this job nests `project` inside `source`.
    #
    # NOTE: SERVER_BIND=any is required. The server binds to loopback by default, and every
    # lab profile drives load from a separate machine, so without it every response is a
    # connection failure - and crank still prints a plausible-looking rps.
    timeout 900 crank \
      --config "$CONFIG" \
      --scenario https --profile "$PROFILE" --relay \
      --application.source.localFolder crank/app \
      --application.source.project src/TlsPoc.CrankServer/TlsPoc.CrankServer.csproj \
      --application.framework net11.0 \
      --application.environmentVariables SERVER_BIND=any \
      --application.environmentVariables TLS_MODE="$mode" \
      ${CPUSET:+--application.cpuSet "$CPUSET"} \
      > "$f" 2>&1

    rps=$(value "$f" "Requests/sec")
    bad=$(value "$f" "Bad responses")
    cores=$(grep -E '^\| Max Cores usage' "$f" | head -1 | sed 's/.*| *\([0-9,]*\) *|.*/\1/' | tr -d ,)
    p99=$(value "$f" "Latency 99th")

    if [ -z "$rps" ]; then
      printf "%-6s %-11s %12s   (see %s)\n" "$i" "$mode" FAILED "$f"
      continue
    fi

    printf "%-6s %-11s %12s %8s %8s %8s\n" "$i" "$mode" "$rps" "${cores:-?}" "${p99:-?}" "${bad:-?}"
    echo "$mode $rps" >> "$OUT/results"
  done
done

[ -f "$OUT/results" ] || { echo "no successful runs"; exit 1; }

echo "--- medians ---"
for mode in sslstream tlssession; do
  grep "^$mode " "$OUT/results" | awk -v m="$mode" '
    {r[NR]=$2}
    END{ if(NR==0){printf "  %-11s no data\n", m; exit}
         asort(r); med=(NR%2)?r[(NR+1)/2]:(r[NR/2]+r[NR/2+1])/2
         printf "  %-11s median=%.0f  min=%.0f  max=%.0f  n=%d\n", m, med, r[1], r[NR], NR }'
done

awk '{a[$1]=a[$1]" "$2}
  END{
    n=split(a["sslstream"],s," "); split(a["tlssession"],t," ")
    if(n==0) exit
    asort(s); asort(t)
    ms=(n%2)?s[(n+1)/2]:(s[n/2]+s[n/2+1])/2
    mt=(n%2)?t[(n+1)/2]:(t[n/2]+t[n/2+1])/2
    printf "  delta       %+.1f%% (tlssession vs sslstream, medians)\n", (mt-ms)/ms*100
  }' "$OUT/results"

echo "full crank output in $OUT/"
