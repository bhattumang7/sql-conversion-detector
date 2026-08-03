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
# Usage: scripts/dotnet-safe.sh test --filter "..."
#        scripts/dotnet-safe.sh build
#        DOTNET_SAFE_TIMEOUT=1200 scripts/dotnet-safe.sh test
set -u

if [ "$#" -eq 0 ]; then
    echo "usage: $0 <dotnet-subcommand> [args...]" >&2
    exit 2
fi

export MSBUILDDISABLENODEREUSE=1

timeout_seconds="${DOTNET_SAFE_TIMEOUT:-900}"
log_dir="${TMPDIR:-/tmp}/dotnet-safe-logs"
mkdir -p "$log_dir"
log_file="$log_dir/$(date +%Y%m%d-%H%M%S 2>/dev/null || echo run)-$$.log"

cleanup() {
    dotnet build-server shutdown >/dev/null 2>&1 || true
}
trap cleanup EXIT

timeout --kill-after=30s "${timeout_seconds}s" dotnet "$@" > "$log_file" 2>&1
exit_code=$?

if [ "$exit_code" -eq 124 ] || [ "$exit_code" -eq 137 ]; then
    echo "dotnet $* TIMED OUT after ${timeout_seconds}s - killed. Full log: $log_file" >&2
elif [ "$exit_code" -ne 0 ]; then
    echo "dotnet $* FAILED (exit $exit_code). Full log: $log_file" >&2
else
    echo "dotnet $* succeeded. Full log: $log_file"
fi

echo "--- last 60 lines of $log_file ---"
tail -n 60 "$log_file"

exit "$exit_code"
