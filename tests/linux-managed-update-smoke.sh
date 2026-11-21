#!/usr/bin/env bash
set -euo pipefail

launcher=$(realpath -- "${1:?Pass the managed launcher path}")
state=$(mktemp -d)
trap 'rm -rf -- "$state"' EXIT
mkdir "$state/install" "$state/data"
install="$state/install"
data="$state/data"
old=release-0.0.1-11111111111111111111111111111111
good=release-0.0.2-22222222222222222222222222222222
bad=release-0.0.3-33333333333333333333333333333333
mkdir "$install/$old" "$install/$good" "$install/$bad"
cat > "$install/$old/Mango" <<'EOF'
#!/bin/bash
echo original
EOF
cat > "$install/$good/Mango" <<'EOF'
#!/bin/bash
set -eu
if [[ "${@: -2:1}" == --update-ready ]]; then
    echo 0.0.2 > "$3/update-ready-${@: -1}"
    sleep 1
fi
echo upgraded
EOF
cat > "$install/$bad/Mango" <<'EOF'
#!/bin/bash
exit 1
EOF
chmod u+x "$install/"*/Mango
printf '%s\n' "$old" > "$install/current"
printf '%s\n' '{"fixture":true}' > "$data/network.json"
printf '%s\n' "$good" > "$install/pending"
[[ $(bash "$launcher" "$install" "$data") == upgraded ]]
[[ $(<"$install/current") == "$good" && $(<"$install/previous") == "$old" ]]
[[ ! -f "$install/pending" && $(<"$data/update-result.json") == *'"failed":false'* ]]
cmp "$data/network.json" "$install/recovery/network.json"
printf '%s\n' "$bad" > "$install/pending"
[[ $(bash "$launcher" "$install" "$data") == upgraded ]]
[[ $(<"$install/current") == "$good" && ! -f "$install/pending" ]]
[[ $(<"$data/update-result.json") == *'"failed":true'* ]]
echo 'Managed Linux upgrade and fallback checks passed.'
