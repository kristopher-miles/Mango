#!/usr/bin/env bash
set -euo pipefail

mango=$(realpath "${1:?Pass the path to the published Mango executable}")
state=$(mktemp -d)
host=
cleanup() {
    if [[ -n "$host" ]]; then
        kill "$host" 2>/dev/null || true
        wait "$host" 2>/dev/null || true
    fi
    rm -rf -- "$state"
}
trap cleanup EXIT

# A long TMPDIR must not prevent the host and control command from connecting.
export TMPDIR="$state/$(printf '%0100d' 0)"
mkdir "$TMPDIR"
"$mango" --data-dir "$state/data" --port 0 --peer-port 55381 &
host=$!
for (( attempt = 0; attempt < 100; attempt++ )); do
    [[ -e "$state/data/network.json" ]] && break
    kill -0 "$host"
    sleep 0.1
done
url=$("$mango" --data-dir "$state/data" --url)
origin=${url%%/#session=*}
session=${url##*#session=}
[[ "$origin" == http://127.0.0.1:* && -n "$session" ]]
[[ $(stat -c %a "$state/data") == 700 ]]
[[ $(stat -c %a "$state/data/network.json") == 600 ]]
[[ $(curl -sS -o /dev/null -w '%{http_code}' "$origin/api/local/settings") == 401 ]]
settings=$(curl -fsS -H "Authorization: Bearer $session" "$origin/api/local/settings")
[[ "$settings" == *'"role":"leech"'* ]]
[[ $(curl -fsS -H "Authorization: Bearer $session" "$origin/api/local/providers") == *'"endpoints":[]'* ]]
[[ $(curl -sS -o /dev/null -w '%{http_code}' -X PUT -H "Authorization: Bearer $session" "$origin/api/local/providers") == 405 ]]
[[ $(curl -sS -o /dev/null -w '%{http_code}' -X PATCH -H "Authorization: Bearer $session" -H 'Content-Type: application/json' \
    --data '{"changes":{"role":"contributor"},"expected":{"role":"leech"}}' "$origin/api/local/settings") == 400 ]]

if "$mango" --data-dir "$state/data" --join 100.64.0.10 71823744-3100-4a7d-a610-4fc993d1928b </dev/null; then
    echo 'Joining while the host is running unexpectedly succeeded.' >&2
    exit 1
fi
if "$mango" --data-dir "$state/data" --pair invalid-pairing-code </dev/null; then
    echo 'An invalid pairing code unexpectedly succeeded.' >&2
    exit 1
fi
"$mango" --data-dir "$state/data" --quit
wait "$host"
host=
[[ ! -e "$state/data/startup-error.txt" ]]
echo 'Linux leech smoke check passed.'
