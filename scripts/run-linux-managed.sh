#!/usr/bin/env bash
set -euo pipefail
umask 077

root=$(realpath -e -- "${1:?Specify the managed installation directory}")
data=$(realpath -e -- "${2:?Specify the Mango data directory}")
shift 2
export MANGO_MANAGED_ROOT="$root"
valid_release() { [[ "$1" =~ ^release-([0-9]+\.[0-9]+\.[0-9]+)-[a-f0-9]{32}$ ]]; }
write_file() {
    printf '%s\n' "$2" > "$1.new-$$"
    mv -fT -- "$1.new-$$" "$1"
}

current=$(<"$root/current")
valid_release "$current"
[[ -x "$root/$current/Mango" && ! -L "$root/$current" ]]
if [[ ! -f "$root/pending" ]]; then
    exec "$root/$current/Mango" --headless --data-dir "$data" "$@"
fi
candidate=$(<"$root/pending")
if ! valid_release "$candidate" || [[ ! -x "$root/$candidate/Mango" || -L "$root/$candidate" ]]; then
    mv -fT -- "$root/pending" "$root/invalid-pending"
    exec "$root/$current/Mango" --headless --data-dir "$data" "$@"
fi
version=${BASH_REMATCH[1]}
if [[ "$candidate" == "$current" ]]; then
    rm -- "$root/pending"
    exec "$root/$current/Mango" --headless --data-dir "$data" "$@"
fi

nonce=$(tr -d '-' < /proc/sys/kernel/random/uuid)
ready="$data/update-ready-$nonce"
backup="$root/recovery"
mkdir -p -- "$backup"
for name in network.json local-settings.json public-ingress.json providers.json; do
    for state in "$data/$name" "$data/$name.last-good"; do
        if [[ -f "$state" && ! -L "$state" ]]; then
            cp -p -- "$state" "$backup/"
        fi
    done
done

child=
on_stop() {
    if [[ -n "$child" ]]; then
        kill -TERM "$child" 2>/dev/null || true
        wait "$child" || true
    fi
    rm -f -- "$ready" "$ready.tmp"
    exit 0
}
trap on_stop TERM INT
"$root/$candidate/Mango" --headless --data-dir "$data" "$@" --update-ready "$nonce" &
child=$!
for (( attempt = 0; attempt < 225; attempt++ )); do
    kill -0 "$child" 2>/dev/null || break
    if [[ -f "$ready" && "$(<"$ready")" == "$version" ]]; then
        obsolete=
        [[ ! -f "$root/previous" ]] || obsolete=$(<"$root/previous")
        write_file "$root/current" "$candidate"
        write_file "$root/previous" "$current"
        rm -- "$root/pending" "$ready"
        write_file "$data/update-result.json" "{\"version\":\"$version\",\"failed\":false,\"message\":\"Installed Mango $version.\"}"
        if valid_release "$obsolete" && [[ "$obsolete" != "$current" && "$obsolete" != "$candidate" &&
            -d "$root/$obsolete" && ! -L "$root/$obsolete" ]]; then
            rm -rf --one-file-system -- "$root/$obsolete"
        fi
        wait "$child"
        exit $?
    fi
    sleep 0.2
done
kill -TERM "$child" 2>/dev/null || true
for (( attempt = 0; attempt < 40; attempt++ )); do
    kill -0 "$child" 2>/dev/null || break
    sleep 0.2
done
kill -KILL "$child" 2>/dev/null || true
wait "$child" 2>/dev/null || true
rm -f -- "$root/pending" "$ready" "$ready.tmp"
write_file "$data/update-result.json" "{\"version\":\"$version\",\"failed\":true,\"message\":\"Mango $version failed its startup check. The previous release was restored.\"}"
exec "$root/$current/Mango" --headless --data-dir "$data" "$@"
