#!/usr/bin/env bash
# Safe wrapper around `dotnet build`/`dotnet test`/etc.
#
# Why this exists: `dotnet build`/`dotnet test` spawn detached MSBuild "node reuse" worker
# processes that inherit the invoking shell's stdout/stderr file descriptors and deliberately
# outlive the command, so a later build can reuse them. Piping either command through another
# process (`| tail`, `| grep`, ...) makes the shell wait for EVERY holder of the pipe's write
# end to close it - the reused nodes never do, so the pipeline hangs forever even though the
# real command finished. Reproduced directly in this repo (2026-08-03): a `dotnet test | tail
# -60` sat for 20+ minutes after `dotnet test` itself had already exited, and repeated
# kill-instead-of-exit across sessions left thousands of orphaned /tmp/MSBuildTemp* dirs.
#
# This script closes both holes: MSBUILDDISABLENODEREUSE=1 stops the worker processes from
# ever being spawned, output is always redirected to a real file (never piped into another
# process), a hard `timeout` guarantees the invocation can't hang indefinitely even so, and a
# trap shuts down any lingering build server on exit regardless of outcome.
#
# A separate, rarer failure mode (also reproduced directly, 2026-08-03): if `dotnet build`
# itself crashes outright (the documented "Internal CLR error 0x80131506" VBCSCompiler race -
# see Directory.Build.props - can still happen on an unlucky overlap even with
# UseSharedCompilation off), its own per-invocation worker processes can be orphaned without
# their parent ever reaping them. These are NOT the persistent "build server" processes
# `dotnet build-server shutdown` manages, so that call alone does not clean them up. `setsid`
# below runs the real command in its own process group so this script's own trap can kill that
# whole group on exit, regardless of whether the command exited, timed out, or crashed.
#
# A THIRD failure mode (reproduced directly, 2026-08-04, in a session with an editor's C#
# language server attached to this repo): `dotnet build-server shutdown` requests a shutdown but
# does not itself block until the VBCSCompiler/MSBuild node processes have actually exited - a
# subsequent invocation launched moments later (this script's own next run, or the language
# server's background build) can start while the previous server is still mid-teardown, hitting
# the exact 0x80131506 race this script otherwise defends against. wait_for_stray_build_processes
# below closes that gap on BOTH ends of every invocation: before launching the real command (so
# this run starts from a genuinely clean slate, not just after a shutdown REQUEST) and after it
# exits (so the NEXT invocation - from this script or anything else - does too). It only ever
# targets VBCSCompiler/MSBuild.dll node-mode workers by name, never the language server's own
# long-lived BuildHost process, which is a legitimate, actively-used part of the editor session.
#
# Even with both ends covered, a genuinely FRESH race is still possible - the language server can
# start its own independent build at the exact instant this script's real command starts theirs,
# with no stale process for the checks above to have caught beforehand (reproduced directly,
# 2026-08-04, immediately after a clean run of this same script). This is the specific,
# documented, external, non-deterministic race Directory.Build.props already accepts as possible
# "on an unlucky overlap even with UseSharedCompilation off" - so the real command is retried
# once, and only once, and only when the exit code and log both match that EXACT signature, never
# for an ordinary build/test failure.
#
# Usage: scripts/dotnet-safe.sh test --filter "..."
#        scripts/dotnet-safe.sh build
#        DOTNET_SAFE_TIMEOUT=1200 scripts/dotnet-safe.sh test
set -u

if [[ "$#" -eq 0 ]]; then
    echo "usage: $0 <dotnet-subcommand> [args...]" >&2
    exit 2
fi

export MSBUILDDISABLENODEREUSE=1

timeout_seconds="${DOTNET_SAFE_TIMEOUT:-900}"
log_dir="${TMPDIR:-/tmp}/dotnet-safe-logs"
mkdir -p "$log_dir"
log_file="$log_dir/$(date +%Y%m%d-%H%M%S 2>/dev/null || echo run)-$$.log"

# Waits (bounded) for any stray VBCSCompiler/MSBuild node-mode worker owned by this user to exit
# on its own after a `build-server shutdown` request; force-kills whatever is left once the
# bound is hit rather than let it keep racing every future invocation - the actual root cause of
# the repeated-crash failure mode above, not a cosmetic cleanup.
wait_for_stray_build_processes() {
    local pattern='VBCSCompiler|MSBuild\.dll.*nodemode'
    local max_wait_seconds=10
    local waited=0
    while pgrep -u "$(id -u)" -f "$pattern" >/dev/null 2>&1; do
        if [[ "$waited" -ge "$max_wait_seconds" ]]; then
            pkill -KILL -u "$(id -u)" -f "$pattern" >/dev/null 2>&1 || true
            break
        fi

        sleep 1
        waited=$((waited + 1))
    done
    return 0
}

dotnet build-server shutdown >/dev/null 2>&1 || true
wait_for_stray_build_processes

group_pid=""
cleanup() {
    if [[ -n "$group_pid" ]]; then
        kill -TERM -- "-$group_pid" >/dev/null 2>&1 || true
        sleep 1
        kill -KILL -- "-$group_pid" >/dev/null 2>&1 || true
    fi
    dotnet build-server shutdown >/dev/null 2>&1 || true
    wait_for_stray_build_processes
    return 0
}
trap cleanup EXIT

max_attempts=2
attempt=1
while :; do
    setsid timeout --kill-after=30s "${timeout_seconds}s" dotnet "$@" > "$log_file" 2>&1 &
    group_pid=$!
    wait "$group_pid"
    exit_code=$?

    if [[ "$exit_code" -eq 134 ]] && [[ "$attempt" -lt "$max_attempts" ]] && grep -q "Internal CLR error" "$log_file" 2>/dev/null; then
        echo "dotnet $* hit the known VBCSCompiler race (attempt ${attempt}/${max_attempts}) - clearing stray processes and retrying..." >&2
        dotnet build-server shutdown >/dev/null 2>&1 || true
        wait_for_stray_build_processes
        attempt=$((attempt + 1))
        log_file="$log_dir/$(date +%Y%m%d-%H%M%S 2>/dev/null || echo run)-$$-retry.log"
        continue
    fi

    break
done

if [[ "$exit_code" -eq 124 ]] || [[ "$exit_code" -eq 137 ]]; then
    echo "dotnet $* TIMED OUT after ${timeout_seconds}s - killed. Full log: $log_file" >&2
elif [[ "$exit_code" -ne 0 ]]; then
    echo "dotnet $* FAILED (exit $exit_code). Full log: $log_file" >&2
else
    echo "dotnet $* succeeded. Full log: $log_file"
fi

echo "--- last 60 lines of $log_file ---"
tail -n 60 "$log_file"

exit "$exit_code"
