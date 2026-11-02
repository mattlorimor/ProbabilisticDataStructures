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
#
# Several mutants at once. Two mutants cannot share one tree, but independent
# copies can run side by side. Give mutation_target MUTATION_JOBS, and the run
# is spread over that many copies instead of one tree:
#
#     MUTATION_JOBS=4 mutation_target ProbabilisticDataStructures
#
# Unset or 1 is the serial mode above, unchanged. With 2 or more:
#
#   - Each copy is a `git clone` of the repository checked out at HEAD, in a
#     directory under ${TMPDIR:-/tmp}. Your tree is never edited, built in or
#     restored; the only thing read from it is the commit.
#   - run_mutation does not run anything. It records the mutation, with the
#     MUTATION_FILTER and MUTATION_CONFIG in effect at that call. The work
#     starts in mutation_done, which prints the verdicts in the order the
#     mutations were given, in the format above, and then the same summary.
#   - Each copy builds the unmodified commit once, so every mutant after that
#     is an incremental build, then claims the next unclaimed mutant until none
#     are left. A shared queue rather than a fixed share, because a mutant that
#     will not compile costs seconds and one that survives costs a whole suite.
#   - Because the copies are made from the commit, mutation_target refuses a
#     change to any tracked file, not only those under the target.
#   - The copies are removed when the run ends, fails, or is interrupted.
#
# A copy is another full build and another full suite run at once on a machine
# that already uses its cores for one, so the gain is well short of the number
# of copies. TESTING.md says when it is worth the disk.

MUTATION_DIR=""
MUTATION_FILTER="${MUTATION_FILTER:-}"
MUTATION_CONFIG="${MUTATION_CONFIG:-Release}"
MUTATION_KILLED=0
MUTATION_SURVIVED=0

# Parallel mode. MUTATION_PARALLEL is the number of copies, and stays 0 in the
# serial mode, in which none of the rest is used.
MUTATION_PARALLEL=0
MUTATION_WORK=""        # scratch directory: the queue, the verdicts and the copies
MUTATION_ROOT=""        # the repository the copies are cloned from
MUTATION_PREFIX=""      # where in it the run was started, so a copy starts there too
MUTATION_HEAD=""        # the commit every copy is checked out at
MUTATION_QUEUED=0
MUTATION_LOST=0         # mutants whose copy died before it gave a verdict
MUTATION_WORKER_PIDS=""

mutation_target() {
    local dir="$1"

    # A refusal has to stay a refusal. Assigning MUTATION_DIR up front left it
    # set after a "refusing to run", so a scratch script that did not check the
    # return code went on to run_mutation, whose first act is the restore.
    MUTATION_DIR=""

    # A parallel run that never reached mutation_done leaves its copies behind
    # until something removes them; a new target is that something.
    mutation__cleanup
    MUTATION_PARALLEL=0

    if [ ! -d "$dir" ]; then
        echo "mutate: no such directory: $dir" >&2
        return 1
    fi

    if ! git rev-parse --git-dir > /dev/null 2>&1; then
        echo "mutate: not a git repository, so nothing could restore the break" >&2
        return 1
    fi

    local copies
    copies="$(mutation__copies)" || return 1

    # The guard. Staged or unstaged changes under the target are exactly what
    # the restore would throw away.
    if ! git diff --quiet -- "$dir" \
        || ! git diff --cached --quiet -- "$dir"; then
        echo "mutate: refusing to run -- '$dir' has uncommitted changes." >&2
        echo >&2
        git status --short -- "$dir" >&2
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
    if [ "$copies" -le 1 ]; then
        untracked="$(git ls-files --others --exclude-standard -- "$dir")"
        if [ -n "$untracked" ]; then
            echo "mutate: note -- untracked files under '$dir':" >&2
            echo "$untracked" | sed 's/^/    /' >&2
            echo "  These survive the restore, but the table will describe a tree" >&2
            echo "  that is not the commit under test." >&2
        fi
    else
        mutation__prepare "$copies" || return 1
    fi

    MUTATION_DIR="$dir"
    if [ "$MUTATION_PARALLEL" -gt 0 ]; then
        # HUP as well, which the serial mode has never needed: closing the
        # terminal there leaves one tree to repair, and here it would leave
        # copies building and testing with nobody to collect them.
        trap 'mutation__cleanup' EXIT
        trap 'mutation__interrupted INT' INT
        trap 'mutation__interrupted TERM' TERM
        trap 'mutation__interrupted HUP' HUP
    else
        trap 'mutation_restore' EXIT INT TERM
    fi
    MUTATION_KILLED=0
    MUTATION_SURVIVED=0
    echo "mutate: target '$MUTATION_DIR', clean. Config $MUTATION_CONFIG."
}

mutation_restore() {
    # Parallel mode never edits this tree, so there is nothing here to put back
    # and `git checkout` would only discard whatever was written since.
    if [ "$MUTATION_PARALLEL" -gt 0 ]; then
        return 0
    fi
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

    if [ "$MUTATION_PARALLEL" -gt 0 ]; then
        mutation__enqueue "$name" "$file" "$old" "$new"
        return $?
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
    if [ "$MUTATION_PARALLEL" -gt 0 ]; then
        mutation__done_parallel
        return $?
    fi

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

# ---------------------------------------------------------------------------
# Parallel mode. Nothing below runs unless mutation_target was given a
# MUTATION_JOBS of 2 or more.
# ---------------------------------------------------------------------------

# How many copies to use: MUTATION_JOBS, or 1 for the serial mode. Prints the
# number. A value that cannot be a count is refused, because falling back to
# serial would hide the typo behind a slow run.
mutation__copies() {
    local jobs="${MUTATION_JOBS:-1}" cpus

    case "$jobs" in
        ''|*[!0-9]*)
            echo "mutate: MUTATION_JOBS must be a whole number, not '$jobs'" >&2
            return 1
            ;;
    esac
    jobs=$((10#$jobs))
    if [ "$jobs" -lt 1 ]; then
        echo "mutate: MUTATION_JOBS must be at least 1; unset or 1 is the serial mode" >&2
        return 1
    fi

    # More copies than CPUs only adds builds for the others to wait behind.
    if [ "$jobs" -gt 1 ]; then
        cpus="$(getconf _NPROCESSORS_ONLN 2>/dev/null)"
        case "$cpus" in
            ''|*[!0-9]*) ;;
            *)
                if [ "$cpus" -ge 1 ] && [ "$jobs" -gt "$cpus" ]; then
                    echo "mutate: MUTATION_JOBS=$jobs is more than the $cpus CPUs here; using $cpus." >&2
                    jobs="$cpus"
                fi
                ;;
        esac
    fi

    echo "$jobs"
}

# Everything mutation_target does for a parallel run once the serial guard on
# the target has passed.
mutation__prepare() {
    local copies="$1" base untracked

    # The guard again, wider. The copies are made from the commit, so a change
    # to any tracked file would be missing from all of them -- the tests
    # included, which are outside the target and which the serial mode runs as
    # they stand. The table would describe a tree nobody has.
    if ! git diff --quiet || ! git diff --cached --quiet; then
        echo "mutate: refusing to run in parallel -- the tree has uncommitted changes." >&2
        echo >&2
        git status --short --untracked-files=no >&2
        echo >&2
        echo "  The copies are made from the commit, not from this tree, so none of" >&2
        echo "  these would be in them. Commit first -- the commit is what you are" >&2
        echo "  verifying anyway -- or leave MUTATION_JOBS unset to mutate in place." >&2
        return 1
    fi

    MUTATION_ROOT="$(git rev-parse --show-toplevel)" || return 1
    MUTATION_PREFIX="$(git rev-parse --show-prefix)" || return 1
    MUTATION_HEAD="$(git rev-parse HEAD)" || return 1

    untracked="$(cd "$MUTATION_ROOT" && git ls-files --others --exclude-standard)"
    if [ -n "$untracked" ]; then
        echo "mutate: note -- untracked files:" >&2
        echo "$untracked" | sed 's/^/    /' >&2
        echo "  The copies are made from the commit, so none of these are in them." >&2
    fi

    base="${TMPDIR:-/tmp}"
    if ! MUTATION_WORK="$(mktemp -d "${base%/}/mutate.XXXXXX")"; then
        MUTATION_WORK=""
        echo "mutate: cannot make a scratch directory under ${base%/}" >&2
        return 1
    fi
    if ! mkdir "$MUTATION_WORK/queue" "$MUTATION_WORK/claim" \
            "$MUTATION_WORK/result" "$MUTATION_WORK/failed"; then
        mutation__cleanup
        return 1
    fi

    MUTATION_QUEUED=0
    MUTATION_PARALLEL="$copies"
    echo "mutate: parallel, up to $copies copies of $MUTATION_HEAD in $MUTATION_WORK; this tree is not touched." >&2
}

# Parallel run_mutation: write the mutation down, and run nothing. The filter
# and configuration are the ones in effect at this call, because the documented
# way to scope one mutation is `MUTATION_FILTER=... run_mutation ...`, and by
# the time the queue runs that assignment is gone.
mutation__enqueue() {
    local q="$MUTATION_WORK/queue/$MUTATION_QUEUED"

    mkdir "$q" || return 1
    printf '%s' "$1" > "$q/name"
    printf '%s' "$2" > "$q/file"
    printf '%s' "$3" > "$q/old"
    printf '%s' "$4" > "$q/new"
    printf '%s' "$MUTATION_FILTER" > "$q/filter"
    printf '%s' "$MUTATION_CONFIG" > "$q/config"
    MUTATION_QUEUED=$((MUTATION_QUEUED + 1))
}

# Read a file into the variable named by $1, byte for byte. A command
# substitution on its own drops trailing newlines, and a pattern may end in one.
mutation__slurp() {
    local _mut_v
    _mut_v="$(cat "$2"; printf x)"
    printf -v "$1" '%s' "${_mut_v%x}"
}

mutation__spawn() {
    local copies="$1" job=1 pid was_monitor=0

    # Job control puts each copy in a process group of its own, which is what
    # lets an interrupt stop a copy's whole tree -- dotnet, its build nodes, the
    # test host -- by group, without a pkill that could name someone else's.
    # Restored afterwards: it is not the caller's to change.
    case $- in *m*) was_monitor=1 ;; esac
    set -m
    while [ "$job" -le "$copies" ]; do
        ( mutation__worker "$job" ) < /dev/null > "$MUTATION_WORK/worker$job.log" 2>&1 &
        pid=$!
        MUTATION_WORKER_PIDS="$MUTATION_WORKER_PIDS $pid"
        job=$((job + 1))
    done
    if [ "$was_monitor" -eq 0 ]; then
        set +m
    fi
}

# One copy, run in a subshell: make it, build it once, then take mutants.
mutation__worker() {
    local job="$1" copy="$MUTATION_WORK/copy$1" idx=0

    # In the copy the ordinary code runs, in its serial mode. The copy is
    # private, so restoring it with git checkout is exactly right.
    MUTATION_PARALLEL=0

    if ! git clone -q --no-checkout "$MUTATION_ROOT" "$copy" \
        || ! cd "$copy" \
        || ! git checkout -q --detach "$MUTATION_HEAD" \
        || ! cd "$copy/$MUTATION_PREFIX"; then
        echo "copy $job could not be made from $MUTATION_HEAD" > "$MUTATION_WORK/failed/$job"
        return 1
    fi

    # Build the unmodified commit first: it makes every mutant's build
    # incremental, and it is the only way to know that a "build failed" below
    # is the mutant's doing and not the commit's.
    if ! dotnet build -c "$MUTATION_CONFIG" > "$MUTATION_WORK/build$job.log" 2>&1; then
        echo "copy $job does not build the commit under test" > "$MUTATION_WORK/failed/$job"
        return 1
    fi

    # mkdir is the claim: it either creates the directory or fails, atomically,
    # and unlike flock it exists on every system this runs on.
    while [ "$idx" -lt "$MUTATION_QUEUED" ]; do
        if mkdir "$MUTATION_WORK/claim/$idx" 2> /dev/null; then
            mutation__run_queued "$idx" "$copy"
        fi
        idx=$((idx + 1))
    done
}

mutation__run_queued() {
    local idx="$1" copy="$2" rc
    local q="$MUTATION_WORK/queue/$1" out="$MUTATION_WORK/result"
    local name file old new filter config

    mutation__slurp name "$q/name"
    mutation__slurp file "$q/file"
    mutation__slurp old "$q/old"
    mutation__slurp new "$q/new"
    mutation__slurp filter "$q/filter"
    mutation__slurp config "$q/config"

    # The whole copy, not only MUTATION_DIR: nothing of the last mutant stays.
    (cd "$copy" && git checkout -q -- .)

    MUTATION_KILLED=0
    MUTATION_SURVIVED=0
    MUTATION_FILTER="$filter" MUTATION_CONFIG="$config" \
        run_mutation "$name" "$file" "$old" "$new" > "$out/$idx.line" 2> "$out/$idx.err"
    rc=$?

    # The status is written last and moved into place, so seeing it means the
    # other two files are complete.
    printf '%s %s %s\n' "$MUTATION_KILLED" "$MUTATION_SURVIVED" "$rc" > "$out/$idx.tmp"
    mv "$out/$idx.tmp" "$out/$idx.status"
}

# Print each verdict once it and every one before it are in, so the output
# reads as a serial run's does. Returns 2 when no copy could build the commit,
# which leaves nothing to report.
mutation__collect() {
    local copies="$1" i=0 pid live job f reported="" failed name
    local killed survived rc

    MUTATION_LOST=0
    while [ "$i" -lt "$MUTATION_QUEUED" ]; do
        # Who is alive is read before the results, so that "nobody is alive"
        # below means they had all finished writing before the files were looked
        # at.
        live=""
        for pid in $MUTATION_WORKER_PIDS; do
            if kill -0 "$pid" 2> /dev/null; then
                live="$live $pid"
            fi
        done
        MUTATION_WORKER_PIDS="$live"

        failed=0
        job=1
        while [ "$job" -le "$copies" ]; do
            f="$MUTATION_WORK/failed/$job"
            if [ -f "$f" ]; then
                failed=$((failed + 1))
                case " $reported " in
                    *" $job "*) ;;
                    *)
                        reported="$reported $job"
                        echo "mutate: $(cat "$f")" >&2
                        if [ -f "$MUTATION_WORK/build$job.log" ]; then
                            tail -n 20 "$MUTATION_WORK/build$job.log" | sed 's/^/    /' >&2
                        fi
                        ;;
                esac
            fi
            job=$((job + 1))
        done

        if [ -f "$MUTATION_WORK/result/$i.status" ]; then
            read -r killed survived rc < "$MUTATION_WORK/result/$i.status"
            if [ -s "$MUTATION_WORK/result/$i.err" ]; then
                cat "$MUTATION_WORK/result/$i.err" >&2
            fi
            cat "$MUTATION_WORK/result/$i.line"
            MUTATION_KILLED=$((MUTATION_KILLED + killed))
            MUTATION_SURVIVED=$((MUTATION_SURVIVED + survived))
            i=$((i + 1))
        elif [ -z "$MUTATION_WORKER_PIDS" ]; then
            if [ "$failed" -ge "$copies" ]; then
                return 2
            fi
            mutation__slurp name "$MUTATION_WORK/queue/$i/name"
            printf '  %-38s NO VERDICT (the copy running it died)\n' "$name"
            MUTATION_LOST=$((MUTATION_LOST + 1))
            i=$((i + 1))
        else
            sleep 1
        fi
    done
}

# Stop every copy that is still running, by process group. The group is the
# worker's own: it was made a group leader when it was started, so what this
# signals is that worker and what it started, and nothing else.
mutation__stop_workers() {
    local pid tries=0 alive

    if [ -z "$MUTATION_WORKER_PIDS" ]; then
        return 0
    fi

    for pid in $MUTATION_WORKER_PIDS; do
        kill -TERM -- "-$pid" 2> /dev/null
    done
    while [ "$tries" -lt 50 ]; do
        alive=0
        for pid in $MUTATION_WORKER_PIDS; do
            if kill -0 -- "-$pid" 2> /dev/null; then
                alive=1
            fi
        done
        if [ "$alive" -eq 0 ]; then
            break
        fi
        sleep 0.1
        tries=$((tries + 1))
    done
    for pid in $MUTATION_WORKER_PIDS; do
        kill -KILL -- "-$pid" 2> /dev/null
        wait "$pid" 2> /dev/null || true
    done
    MUTATION_WORKER_PIDS=""
}

# Idempotent, and the only thing that removes the copies: success, failure,
# a new mutation_target and the EXIT trap all end here.
mutation__cleanup() {
    mutation__stop_workers
    if [ -n "$MUTATION_WORK" ]; then
        rm -rf "$MUTATION_WORK"
        MUTATION_WORK=""
    fi
}

mutation__interrupted() {
    trap - EXIT INT TERM HUP
    echo "mutate: $1 -- stopping the copies and removing them." >&2
    mutation__cleanup
    # Die of the signal rather than carry on: the caller's script would
    # otherwise go on to its next run_mutation.
    kill -s "$1" $$
}

mutation__done_parallel() {
    local copies="$MUTATION_PARALLEL" rc=0 lost pid

    if [ "$copies" -gt "$MUTATION_QUEUED" ]; then
        copies="$MUTATION_QUEUED"
    fi

    MUTATION_LOST=0
    if [ "$copies" -gt 0 ]; then
        mutation__spawn "$copies"
        mutation__collect "$copies" || rc=$?
        # Every verdict is in, so the copies are on their way out; let them go
        # rather than signal anything.
        for pid in $MUTATION_WORKER_PIDS; do
            wait "$pid" 2> /dev/null || true
        done
        MUTATION_WORKER_PIDS=""
    fi
    lost="$MUTATION_LOST"

    mutation__cleanup
    trap - EXIT INT TERM HUP
    MUTATION_PARALLEL=0
    MUTATION_DIR=""

    if [ "$rc" -eq 2 ]; then
        echo "mutate: no copy could build the commit under test -- check it by hand" >&2
        return 1
    fi

    # "Restored and building" is true by construction here: this tree was never
    # edited, and every copy built the commit before it took a mutant.
    echo "mutate: $MUTATION_KILLED killed, $MUTATION_SURVIVED survived; tree restored and building."
    if [ "$MUTATION_SURVIVED" -gt 0 ]; then
        echo "mutate: survivors are leads, not verdicts. Hand-apply each before believing it."
    fi
    if [ "$lost" -gt 0 ]; then
        echo "mutate: $lost mutation(s) got no verdict because their copy died; run them again." >&2
        return 1
    fi
}
