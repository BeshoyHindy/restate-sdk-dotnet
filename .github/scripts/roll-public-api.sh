#!/usr/bin/env bash
set -euo pipefail

# Moves every PublicAPI.Unshipped.txt entry into the PublicAPI.Shipped.txt beside it, and
# applies the *REMOVED* entries by deleting the symbols they name from the shipped file.
# Run on the release pull request, where the version and changelog already change: the API
# that PR releases stops being unshipped the moment it is published.
# Usage: roll-public-api.sh [root-directory]   (defaults to the repository root)

ROOT="${1:-$(cd "$(dirname "$0")/../.." && pwd)}"
HEADER="#nullable enable"
rolled=0

# Strips the nullable header and blank lines, leaving the API entries.
entries_of() {
    grep -v -e "^$HEADER\$" -e '^[[:space:]]*$' "$1" || true
}

# An entry marked *REMOVED* records that a shipped API is gone. It deletes the symbol from the
# shipped file instead of being copied into it: the analyzer rejects the marker there (RS0024),
# and the entry it names no longer exists.
removals_of() {
    entries_of "$1" | sed -n 's/^\*REMOVED\*//p'
}

additions_of() {
    entries_of "$1" | grep -v '^\*REMOVED\*' || true
}

while IFS= read -r unshipped; do
    shipped="$(dirname "$unshipped")/PublicAPI.Shipped.txt"
    if [ ! -f "$shipped" ]; then
        echo "No PublicAPI.Shipped.txt beside $unshipped" >&2
        exit 1
    fi

    unshipped_entries="$(entries_of "$unshipped")"
    if [ -z "$unshipped_entries" ]; then
        continue
    fi

    removals="$(removals_of "$unshipped")"
    kept="$(entries_of "$shipped")"
    if [ -n "$removals" ]; then
        kept="$(printf '%s\n' "$kept" | grep -F -x -v -f <(printf '%s\n' "$removals") || true)"
    fi

    {
        echo "$HEADER"
        printf '%s\n%s\n' "$kept" "$(additions_of "$unshipped")" \
            | grep -v '^[[:space:]]*$' | LC_ALL=C sort -u
    } > "$shipped.rolled"
    mv "$shipped.rolled" "$shipped"

    echo "$HEADER" > "$unshipped"

    count="$(printf '%s\n' "$unshipped_entries" | wc -l | tr -d ' ')"
    rolled=$((rolled + count))
    echo "Rolled $count entries into ${shipped#"$ROOT"/}"
done < <(find "$ROOT/src" -name PublicAPI.Unshipped.txt | LC_ALL=C sort)

if [ "$rolled" -eq 0 ]; then
    echo "No unshipped public API entries to roll."
fi
