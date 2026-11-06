#!/bin/bash
# DIAGNOSTIC BRANCH ONLY (#350's intermittent test host crash), not for merging.
# Run the full suite again and again on macOS until the test host crashes, and keep what the
# crash leaves: the console log, the runner's diagnostic logs, the runtime's dump and crash
# report, and the names of any macOS crash reports written at the time.
# usage, from the repository root: scripts/crash-hunt-macos.sh [runs]   (default 60)
set -u
runs=${1:-60}
root=$(cd "$(dirname "$0")/.." && pwd)
out=$root/crash-hunt
mkdir -p "$out"
cd "$root" || exit 1
command -v dotnet > /dev/null || { echo "dotnet not found: install the .NET 10 SDK first"; exit 1; }
echo "building..."
dotnet build -c Release -warnaserror > "$out/build.log" 2>&1 || { echo "build failed; see $out/build.log"; exit 1; }
for i in $(seq 1 "$runs"); do
  run=$out/run$i
  rm -rf "$run"
  mkdir -p "$run"
  touch "$run/started"
  # Three processors, as GitHub's macOS runner has: the suite then runs three tests at a time.
  DOTNET_PROCESSOR_COUNT=3 \
  DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=1 DOTNET_EnableCrashReport=1 \
  DOTNET_DbgMiniDumpName="$run/core.%p" \
    dotnet test -c Release --no-build --logger "console;verbosity=normal" \
    --diag "$run/diag.txt" > "$run/console.log" 2>&1
  if grep -q 'Test host process crashed' "$run/console.log" || ls "$run"/core.* > /dev/null 2>&1; then
    echo "run $i: CRASHED. Everything it left is in $run"
    find ~/Library/Logs/DiagnosticReports -newer "$run/started" -type f 2>/dev/null \
      | tee "$run/macos-crash-reports.txt"
    exit 0
  fi
  echo "run $i: $(grep -E '^ *(Total tests|Passed|Failed):' "$run/console.log" | tr -s ' ' | tr '\n' ' ')"
  rm -rf "$run"
done
echo "no crash in $runs runs"
