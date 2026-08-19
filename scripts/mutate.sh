# Helpers for step 3 of the loop in TESTING.md: break the implementation on
# purpose, watch the tests fail, put it back.
#
# Sourced rather than run, because a mutation run is a handful of one-off edits
# whose text belongs next to the commit it is verifying:
#
#     source scripts/mutate.sh
#     mutation_target ProbabilisticDataStructures
#
#     run_mutation "alpha 0.673 -> 0.697" \
#         ProbabilisticDataStructures/HyperLogLog.cs \
#         'return 0.673;' 'return 0.697;'
#
#     mutation_done
#
# Each run restores the target directory, applies one edit, builds, runs the
# tests, and prints the verdict. Set MUTATION_FILTER to scope the test run:
#
#     MUTATION_FILTER='FullyQualifiedName~TestHyperLogLog' source scripts/mutate.sh
#
# The restore is `git checkout -- <dir>`, which discards *every* uncommitted
# change in that directory and not only the deliberate break. That is why
# mutation_target refuses to start on a dirty tree. The rule it enforces was
# learned three times: twice by losing a nearly finished change to its own
# verification step, and once by losing a fix and then reporting a mutation
# result measured against the tree without it.

MUTATION_DIR=""
MUTATION_FILTER="${MUTATION_FILTER:-}"
MUTATION_CONFIG="${MUTATION_CONFIG:-Release}"
MUTATION_KILLED=0
MUTATION_SURVIVED=0

mutation_target() {
    MUTATION_DIR="$1"

    if [ ! -d "$MUTATION_DIR" ]; then
        echo "mutate: no such directory: $MUTATION_DIR" >&2
        return 1
    fi

    if ! git rev-parse --git-dir > /dev/null 2>&1; then
        echo "mutate: not a git repository, so nothing could restore the break" >&2
        return 1
    fi

    # The guard. Staged or unstaged changes under the target are exactly what
    # the restore would throw away.
    if ! git diff --quiet -- "$MUTATION_DIR" \
        || ! git diff --cached --quiet -- "$MUTATION_DIR"; then
        echo "mutate: refusing to run -- '$MUTATION_DIR' has uncommitted changes." >&2
        echo >&2
        git status --short -- "$MUTATION_DIR" >&2
        echo >&2
        echo "  Restoring a mutation discards everything uncommitted in that" >&2
        echo "  directory, not only the deliberate break. Commit the work first;" >&2
        echo "  the commit is what you are verifying anyway." >&2
        return 1
    fi

    # Untracked files survive the restore, so they are not in danger -- but they
    # usually mean a change is half made, and a mutation table measured now
    # describes a tree nobody will have again.
    local untracked
    untracked="$(git ls-files --others --exclude-standard -- "$MUTATION_DIR")"
    if [ -n "$untracked" ]; then
        echo "mutate: note -- untracked files under '$MUTATION_DIR':" >&2
        echo "$untracked" | sed 's/^/    /' >&2
        echo "  These survive the restore, but the table will describe a tree" >&2
        echo "  that is not the commit under test." >&2
    fi

    trap 'mutation_restore' EXIT INT TERM
    MUTATION_KILLED=0
    MUTATION_SURVIVED=0
    echo "mutate: target '$MUTATION_DIR', clean. Config $MUTATION_CONFIG."
}

mutation_restore() {
    if [ -n "$MUTATION_DIR" ]; then
        git checkout -- "$MUTATION_DIR" 2>/dev/null
    fi
}

run_mutation() {
    local name="$1" file="$2" old="$3" new="$4"

    if [ -z "$MUTATION_DIR" ]; then
        echo "mutate: call mutation_target before run_mutation" >&2
        return 1
    fi

    mutation_restore

    if ! MUTATION_FILE="$file" MUTATION_OLD="$old" MUTATION_NEW="$new" python3 -c '
import os, sys
path = os.environ["MUTATION_FILE"]
old, new = os.environ["MUTATION_OLD"], os.environ["MUTATION_NEW"]
try:
    src = open(path, encoding="utf-8").read()
except OSError as e:
    sys.exit(f"cannot read {path}: {e}")
found = src.count(old)
if found != 1:
    sys.exit(f"pattern appears {found} times, and a mutation must name one place")
open(path, "w", encoding="utf-8").write(src.replace(old, new))
'; then
        printf '  %-38s PATTERN ERROR\n' "$name"
        return 1
    fi

    # A mutation that will not compile is caught, but say so rather than
    # counting it beside one the tests actually noticed.
    if ! dotnet build -c "$MUTATION_CONFIG" > /dev/null 2>&1; then
        printf '  %-38s caught (build failed)\n' "$name"
        MUTATION_KILLED=$((MUTATION_KILLED + 1))
        return 0
    fi

    local output
    if [ -n "$MUTATION_FILTER" ]; then
        output="$(dotnet test -c "$MUTATION_CONFIG" --no-build --filter "$MUTATION_FILTER" 2>&1 | tail -1)"
    else
        output="$(dotnet test -c "$MUTATION_CONFIG" --no-build 2>&1 | tail -1)"
    fi

    local counts
    counts="$(echo "$output" | sed -n 's/.*Failed: *\([0-9]*\), Passed: *\([0-9]*\).*/\1 of \1+\2/p')"
    if echo "$output" | grep -q "^Failed!"; then
        printf '  %-38s killed   %s\n' "$name" \
            "$(echo "$output" | sed -n 's/.*Failed: *\([0-9]*\),.*/(\1 tests)/p')"
        MUTATION_KILLED=$((MUTATION_KILLED + 1))
    else
        printf '  %-38s SURVIVED %s\n' "$name" \
            "$(echo "$output" | sed -n 's/.*Total: *\([0-9]*\).*/(all \1 passed)/p')"
        MUTATION_SURVIVED=$((MUTATION_SURVIVED + 1))
    fi
}

mutation_done() {
    mutation_restore
    trap - EXIT INT TERM

    if ! dotnet build -c "$MUTATION_CONFIG" > /dev/null 2>&1; then
        echo "mutate: the tree does NOT build after restoring -- check it by hand" >&2
        return 1
    fi

    echo "mutate: $MUTATION_KILLED killed, $MUTATION_SURVIVED survived; tree restored and building."
    if [ "$MUTATION_SURVIVED" -gt 0 ]; then
        echo "mutate: survivors are leads, not verdicts. Hand-apply each before believing it."
    fi
    MUTATION_DIR=""
}
