#!/bin/bash
# Stryker, one library file at a time: the way a sweep finishes. TESTING.md ("Mutation
# testing") has the measurements behind each step and what a sweep costs.
#
#     scripts/stryker-file.sh [options] <library file>... [-- <stryker options>]
#
#     scripts/stryker-file.sh ProbabilisticDataStructures/Buckets.cs --concurrency 28
#     scripts/stryker-file.sh --out ~/sweeps ProbabilisticDataStructures/Buckets.cs \
#         ProbabilisticDataStructures/SlidingWindow.cs
#
# Options:
#   --concurrency N  Stryker test sessions at once, shared out among the files, which run
#                    side by side. Default: one per CPU.
#   --jobs N         test methods run at once while finding which tests reach each file.
#                    Default: one per CPU.
#   --out DIR        where the reports go; it must be empty or new. Default: a new
#                    directory under ${TMPDIR:-/tmp}, named when the run ends.
#   --framework TFM  the target framework whose build is swept. Default: net10.0. One,
#                    because Stryker picks one at random for a project with several, and
#                    net10.0 because every sweep recorded before net11.0 was added ran
#                    there, so a new one stays comparable with them.
#   -- ...           everything after it goes to every Stryker run as it is.
#
# For each file, <out>/<name>/ ends up holding Stryker's reports/mutation-report.json and
# .html, its console log, the test filter it ran with, and summary.txt: the mutants by
# verdict, then each Survived, Timeout and NoCoverage one with its line and mutation.
# Those are leads, not verdicts: every one still has to be applied by hand and run
# against the whole suite (mutate.sh) before it is believed.
#
# What it does, all of it in copies of HEAD in a scratch directory that is removed when
# the run ends, so this tree is never built in or edited. What is measured is the
# commit, so like mutate.sh it refuses to start on uncommitted changes to tracked files.
#
#   1. The suite, built and run once in a copy. It has to pass, and its results are the
#      roster of test methods: a list taken from the run cannot leave a method out.
#   2. Which tests reach each file. Every test method runs alone under the code
#      coverage collector, and the ones that execute a line of a file are that file's
#      test filter. Alone, because a method can reach a file only through shared state
#      built by whichever test asks for it first, and run together only that first test
#      shows it. A method that fails alone or leaves no report is kept for every file,
#      and named: a filter that leaves out a test that reaches the file makes its
#      mutants survive falsely, so a doubt is settled by keeping the test.
#   3. Only the file is instrumented. Stryker mutates every file in the project and
#      drops the mutants outside --mutate only after building them in, so every file's
#      code runs under mutant switches and coverage capture, and a test that never
#      touches the file is dozens of times slower than it is. In each file's copy, every
#      other library file begins with "// Stryker disable all", which keeps its mutants
#      out of the build. Nothing committed carries the comment.
#   4. Stryker, in that copy, with the repository's stryker-config.json, the file as
#      the scope and the filter as test-case-filter.

set -u

die() {
    echo "stryker-file: $*" >&2
    exit 1
}

usage() {
    sed -n '5,28p' "$0" | sed 's/^# \{0,1\}//' >&2
    exit 2
}

cpus="$(getconf _NPROCESSORS_ONLN 2>/dev/null || echo 4)"
concurrency="$cpus"
jobs="$cpus"
out=""
framework="net10.0"
targets=()
stryker_args=()

while [ $# -gt 0 ]; do
    case "$1" in
        --concurrency) [ $# -ge 2 ] || usage; concurrency="$2"; shift 2 ;;
        --jobs) [ $# -ge 2 ] || usage; jobs="$2"; shift 2 ;;
        --out) [ $# -ge 2 ] || usage; out="$2"; shift 2 ;;
        --framework) [ $# -ge 2 ] || usage; framework="$2"; shift 2 ;;
        --) shift; stryker_args=("$@"); break ;;
        -h|--help) usage ;;
        -*) die "unknown option $1 (Stryker's own options go after --)" ;;
        *) targets+=("$1"); shift ;;
    esac
done
[ "${#targets[@]}" -ge 1 ] || usage
for n in "$concurrency" "$jobs"; do
    case "$n" in
        ''|*[!0-9]*|0) die "--concurrency and --jobs take a whole number of 1 or more, not '$n'" ;;
    esac
done

# The output directory is resolved where it was named, before the move to the top of
# the repository; so are the files.
if [ -n "$out" ]; then
    mkdir -p "$out" || die "cannot make $out"
    out="$(cd "$out" && pwd)" || exit 1
    [ -z "$(ls -A "$out")" ] || die "$out is not empty"
fi
root="$(git rev-parse --show-toplevel 2>/dev/null)" || die "not inside a git repository"
rel=()
for t in "${targets[@]}"; do
    r="$(ROOT="$root" T="$t" python3 -c '
import os
print(os.path.relpath(os.path.abspath(os.environ["T"]), os.environ["ROOT"]))')" || exit 1
    rel+=("$r")
done
cd "$root" || die "cannot enter $root"

names=()
for t in "${rel[@]}"; do
    case "$t" in
        ProbabilisticDataStructures/*.cs) ;;
        *) die "not a library file (ProbabilisticDataStructures/<name>.cs): $t" ;;
    esac
    git ls-files --error-unmatch -- "$t" > /dev/null 2>&1 || die "$t is not a tracked file"
    name="$(basename "$t" .cs)"
    for n in ${names[@]+"${names[@]}"}; do
        [ "$n" != "$name" ] || die "$name is named twice"
    done
    names+=("$name")
done
if ! git diff --quiet || ! git diff --cached --quiet; then
    git status --short --untracked-files=no >&2
    die "uncommitted changes. The sweep measures HEAD, so they would not be in it; commit first."
fi

work="$(mktemp -d "${TMPDIR:-/tmp}/stryker-file.XXXXXX")" || die "cannot make a scratch directory"
if [ -z "$out" ]; then
    out="$(mktemp -d "${TMPDIR:-/tmp}/stryker-file-out.XXXXXX")" || die "cannot make an output directory"
fi

# Each Stryker run is started in a process group of its own, so that an interrupt can
# stop it and everything it started -- its test hosts above all -- without a pkill that
# could name someone else's.
pids=""
tmp=""
stop_runs() {
    local pid
    for pid in $pids; do
        kill -TERM -- "-$pid" 2> /dev/null
    done
    for pid in $pids; do
        wait "$pid" 2> /dev/null
    done
    pids=""
}
cleanup() {
    stop_runs
    rm -rf "$work"
    [ -z "$tmp" ] || rm -rf "$tmp"
}
trap cleanup EXIT
trap 'echo "stryker-file: interrupted; stopping the runs and removing $work" >&2; exit 130' INT TERM HUP

# What the runs write to TMPDIR goes in a directory of the script's own, removed with
# the copies, and not in the caller's TMPDIR: the code coverage collector leaves an
# empty CodeCoverage.<guid> directory behind for every run, which is one for each test
# method. The directory has to be short. The collector listens on a socket at
# $TMPDIR/<guid>/.pipe, a socket's path holds at most 107 bytes, and a run whose TMPDIR
# is longer than 64 characters hangs without a word before its tests start. So it is
# inside the scratch directory where that is short enough, and under /tmp where not.
if [ "${#work}" -le 54 ]; then
    tmp="$work/t"
    mkdir "$tmp" || die "cannot make $tmp"
else
    tmp="$(mktemp -d /tmp/sf.XXXXXX)" || die "cannot make a short temporary directory under /tmp"
fi
mkdir "$tmp/m" || die "cannot make $tmp/m"
export TMPDIR="$tmp/m"
# The builds, Stryker's among them, would otherwise leave a compiler server and build
# nodes running for minutes afterwards, listening in that directory after it is gone.
export MSBUILDDISABLENODEREUSE=1 UseSharedCompilation=false

head_sha="$(git rev-parse --short HEAD)"
copy_head() {
    mkdir -p "$1" && git archive --format=tar HEAD | tar -x -C "$1"
}

# 1. The suite, once.
echo "stryker-file: $head_sha; scratch $work; reports $out"
base="$work/base"
copy_head "$base" || die "cannot copy HEAD"
echo "stryker-file: building and running the suite"
(cd "$base" && dotnet build -c Release) > "$out/build.log" 2>&1 \
    || die "the commit does not build; see $out/build.log"
# The framework is checked against what the build wrote, not a list kept here.
[ -d "$base/TestProbabilisticDataStructures/bin/Release/$framework" ] \
    || die "the test project has no $framework build; it has: $(ls "$base/TestProbabilisticDataStructures/bin/Release" | tr '\n' ' ')"
echo "stryker-file: sweeping the $framework build"
(cd "$base" && dotnet test -c Release --no-build --framework "$framework" \
    --logger "trx;LogFileName=$work/suite.trx") \
    > "$out/suite.log" 2>&1 \
    || die "the suite does not pass; see $out/suite.log"

reach="$work/reach"
mkdir -p "$reach/methods" "$reach/lock"
TRX="$work/suite.trx" python3 - > "$reach/roster.txt" <<'EOF' || die "cannot read the suite's results"
import os, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(os.environ["TRX"]).getroot()
names = {m.get("className") + "." + m.get("name") for m in root.iterfind(".//t:TestMethod", ns)}
print("\n".join(sorted(names)))
EOF

# 2. Which tests reach each file. The collector is the one the test project already
# references. It instruments the assemblies it is given in place, so each run going at
# once has its own copy of the build output, and it is told to instrument the library
# alone, which halves the cost of a run that is mostly startup.
adapter="$(ASSETS="$base/TestProbabilisticDataStructures/obj/project.assets.json" python3 - <<'EOF'
import os, json
a = json.load(open(os.environ["ASSETS"]))
lib = next(v["path"] for k, v in a["libraries"].items() if k.lower().startswith("microsoft.codecoverage/"))
print(os.path.join(a["project"]["restore"]["packagesPath"], lib, "build", "netstandard2.0"))
EOF
)" || die "cannot find the code coverage collector"
[ -d "$adapter" ] || die "no code coverage collector at $adapter"
cat > "$reach/coverage.runsettings" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<RunSettings>
  <DataCollectionRunSettings>
    <DataCollectors>
      <DataCollector friendlyName="Code Coverage">
        <Configuration>
          <Format>cobertura</Format>
          <CodeCoverage>
            <ModulePaths>
              <Include>
                <ModulePath>.*[\\/]ProbabilisticDataStructures\.dll$</ModulePath>
              </Include>
            </ModulePaths>
          </CodeCoverage>
        </Configuration>
      </DataCollector>
    </DataCollectors>
  </DataCollectionRunSettings>
</RunSettings>
EOF
i=1
while [ "$i" -le "$jobs" ]; do
    cp -a "$base/TestProbabilisticDataStructures/bin/Release/$framework" "$reach/slot$i" \
        || die "cannot copy the build output"
    i=$((i + 1))
done
printf '%s\n' "${rel[@]}" > "$reach/targets.txt"
echo "stryker-file: running $(wc -l < "$reach/roster.txt" | tr -d ' ') test methods alone, $jobs at once"

reach_one() {
    local method="$1" slot="" i dir rc report
    while [ -z "$slot" ]; do
        i=1
        while [ "$i" -le "$REACH_JOBS" ]; do
            if mkdir "$REACH_DIR/lock/$i" 2> /dev/null; then
                slot="$i"
                break
            fi
            i=$((i + 1))
        done
        [ -n "$slot" ] || sleep 0.2
    done
    dir="$REACH_DIR/methods/$method"
    mkdir -p "$dir"
    # A TMPDIR of the run's own, emptied when it ends: what the collector leaves is gone
    # before the slot's next run, and the path stays short (above).
    rm -rf "$REACH_TMP/$slot"
    mkdir -p "$REACH_TMP/$slot"
    TMPDIR="$REACH_TMP/$slot" dotnet test "$REACH_DIR/slot$slot/TestProbabilisticDataStructures.dll" \
        --test-adapter-path "$REACH_ADAPTER" \
        --filter "FullyQualifiedName=$method" \
        --settings "$REACH_DIR/coverage.runsettings" --collect "Code Coverage" \
        --results-directory "$dir" > "$dir/test.log" 2>&1
    rc=$?
    rm -rf "$REACH_TMP/$slot"
    rmdir "$REACH_DIR/lock/$slot"
    report="$(find "$dir" -name '*.cobertura.xml' | head -n 1)"
    # One line per file: the lines of it the method executed. The report itself runs to
    # megabytes, and is not kept.
    REPORT="$report" RC="$rc" TARGETS="$REACH_DIR/targets.txt" python3 - > "$dir/result" <<'EOF'
import os, xml.etree.ElementTree as ET
targets = [os.path.normpath(t.strip()) for t in open(os.environ["TARGETS"]) if t.strip()]
if os.environ["RC"] != "0" or not os.environ["REPORT"]:
    why = "failed alone" if os.environ["RC"] != "0" else "left no coverage report"
    for t in targets:
        print(f"{t}\tkept\t{why}")
    raise SystemExit
hit = {t: set() for t in targets}
for _, el in ET.iterparse(os.environ["REPORT"]):
    if el.tag == "class":
        f = os.path.normpath(el.get("filename", ""))
        for t in targets:
            if f.endswith(os.sep + t):
                hit[t] |= {ln.get("number") for ln in el.iter("line") if int(ln.get("hits", "0")) > 0}
        el.clear()
for t in targets:
    print(f"{t}\t{len(hit[t])}")
EOF
    [ -z "$report" ] || rm -f "$report"
}
export -f reach_one
export REACH_DIR="$reach" REACH_JOBS="$jobs" REACH_ADAPTER="$adapter" REACH_TMP="$tmp"
# In the background and in a group of its own, like the Stryker runs below, so that an
# interrupt is acted on at once, and stops it, rather than after every method has run.
set -m
xargs -P "$jobs" -I{} bash -c 'reach_one "$1"' _ {} < "$reach/roster.txt" &
pids="$!"
set +m
wait "$pids"
pids=""
rm -rf "$reach"/slot*
missing="$(while read -r m; do [ -f "$reach/methods/$m/result" ] || echo "$m"; done < "$reach/roster.txt")"
[ -z "$missing" ] || die "no result for $(echo "$missing" | wc -l | tr -d ' ') test method(s), the first $(echo "$missing" | head -n 1)"
echo "stryker-file: every test method ran alone"

# 3 and 4: a copy per file, the filter and the config, and Stryker, side by side.
share=$((concurrency / ${#rel[@]}))
[ "$share" -ge 1 ] || share=1
set -m
k=0
for t in "${rel[@]}"; do
    name="${names[$k]}"
    k=$((k + 1))
    dest="$out/$name"
    tree="$work/$name"
    mkdir -p "$dest"
    copy_head "$tree" || die "cannot copy HEAD for $name"
    REACH="$reach" TREE="$tree" TARGET="$t" DEST="$dest" FRAMEWORK="$framework" python3 - <<'EOF' || die "cannot write the config for $t"
import os, json, glob
reach, tree, target, dest, framework = (os.environ[k] for k in ("REACH", "TREE", "TARGET", "DEST", "FRAMEWORK"))
for path in glob.glob(os.path.join(tree, "ProbabilisticDataStructures", "**", "*.cs"), recursive=True):
    if os.path.normpath(os.path.relpath(path, tree)) != os.path.normpath(target):
        text = open(path, encoding="utf-8-sig").read()
        open(path, "w", encoding="utf-8").write("// Stryker disable all\n" + text)
roster = [l.strip() for l in open(os.path.join(reach, "roster.txt")) if l.strip()]
keep, doubtful = [], []
for m in roster:
    for line in open(os.path.join(reach, "methods", m, "result")):
        f, what, *why = line.rstrip("\n").split("\t")
        if os.path.normpath(f) != os.path.normpath(target):
            continue
        if what == "kept":
            keep.append(m)
            doubtful.append(f"{m} ({why[0]})")
        elif int(what) > 0:
            keep.append(m)
if not keep:
    raise SystemExit(f"stryker-file: no test executes a line of {target}")
open(os.path.join(dest, "tests.txt"), "w").write("\n".join(keep) + "\n")
for d in doubtful:
    print(f"stryker-file: {os.path.basename(target)}: kept, since its run alone says nothing: {d}")
path = os.path.join(tree, "TestProbabilisticDataStructures", "stryker-config.json")
config = json.load(open(path))
c = config["stryker-config"]
c["mutate"] = ["**/" + os.path.basename(target)]
# The build the tests that reach the file were found in; left out, Stryker picks one.
c["target-framework"] = framework
c["test-case-filter"] = "|".join(f"FullyQualifiedName={m}" for m in keep)
c["reporters"] = sorted(set(c.get("reporters", [])) | {"json", "html"})
json.dump(config, open(os.path.join(tree, "TestProbabilisticDataStructures", "stryker-file.json"), "w"), indent=2)
print(f"stryker-file: {os.path.basename(target)}: {len(keep)} of {len(roster)} test methods execute it")
EOF
    (
        cd "$tree/TestProbabilisticDataStructures" || exit 1
        dotnet tool restore > "$dest/tool-restore.log" 2>&1 || exit 1
        dotnet stryker -f stryker-file.json -O "$dest" --concurrency "$share" \
            ${stryker_args[@]+"${stryker_args[@]}"} > "$dest/stryker.log" 2>&1
    ) < /dev/null &
    pids="$pids $!"
    echo "stryker-file: $name: Stryker started, $share test sessions at once; its log is $dest/stryker.log"
done
set +m

failed=0
for pid in $pids; do
    wait "$pid" || failed=$((failed + 1))
done
pids=""

# The summaries.
k=0
for t in "${rel[@]}"; do
    name="${names[$k]}"
    k=$((k + 1))
    dest="$out/$name"
    REPORT="$dest/reports/mutation-report.json" TARGET="$t" python3 - > "$dest/summary.txt" <<'EOF'
import os, json, collections
target = os.environ["TARGET"]
try:
    r = json.load(open(os.environ["REPORT"]))
except OSError:
    raise SystemExit(f"{target}: no report; see stryker.log beside this file")
f = next(k for k in r["files"] if os.path.normpath(k).endswith(os.sep + os.path.normpath(target)))
mutants = r["files"][f]["mutants"]
order = ["Killed", "Survived", "Timeout", "NoCoverage", "RuntimeError", "CompileError", "Ignored"]
counts = collections.Counter(m["status"] for m in mutants)
print(f"{target}: {len(mutants)} mutants. " + ", ".join(
    f"{s} {counts[s]}" for s in order + sorted(set(counts) - set(order)) if counts[s]))
src = r["files"][f]["source"].split("\n")
starts = [0]
for line in src:
    starts.append(starts[-1] + len(line) + 1)
text = "\n".join(src)
def at(p):
    return starts[p["line"] - 1] + p["column"] - 1
def change(was, now, room=60):
    # Short ones whole. A long one by what the mutation changed, with a little either
    # side: an expression whose last operator flips would otherwise print the same twice.
    if len(was) <= room + 20 and len(now) <= room + 20:
        return was, now
    p = 0
    while p < min(len(was), len(now)) and was[p] == now[p]:
        p += 1
    s = 0
    while s < min(len(was), len(now)) - p and was[-1 - s] == now[-1 - s]:
        s += 1
    a, b = max(0, p - 12), 12
    def cut(x):
        mid = x[a:len(x) - max(0, s - b)]
        return ("..." if a else "") + (mid if len(mid) <= room else mid[:room] + "...") \
            + ("..." if s > b else "")
    return cut(was), cut(now)
leads = [m for m in mutants if m["status"] in ("Survived", "Timeout", "NoCoverage")]
if leads:
    print("Leads, each to be applied by hand and run against the whole suite before it is believed:")
for m in sorted(leads, key=lambda m: (m["location"]["start"]["line"], m["location"]["start"]["column"])):
    loc = m["location"]
    was, now = change(" ".join(text[at(loc["start"]):at(loc["end"])].split()),
                      " ".join((m.get("replacement") or "").split()))
    print(f"  {m['status']:<10} {os.path.basename(target)}:{loc['start']['line']}  "
          f"{m['mutatorName']}: {was} -> {now}")
EOF
    cat "$dest/summary.txt"
done

echo "stryker-file: reports in $out"
[ "$failed" -eq 0 ] || die "$failed Stryker run(s) failed; see stryker.log in $out"
