#!/bin/bash
# End-to-end test of the in-app updater against a local feed.
#
# Builds two signed bundles in a scratch directory (an "installed" 1.0.0 and a
# "released" 99.9.9), serves the newer one as a DMG over 127.0.0.1, and drives
# the whole download, verify, and swap path through the CLI.
#
# It never touches ~/Applications, and the scratch bundles carry their own
# bundle identifier, so a running Patchthrough neither blocks the test nor gets
# replaced by it.
#
# Covered: feed read, asset selection, checksum, signature and team, bundle
# identity, version predicates, the swap, the janitor, and the refusal cases.
# Not covered: the Gatekeeper assessment (this image is signed, not notarized,
# so the run sets PATCHTHROUGH_UPDATE_SKIP_GATEKEEPER=1 and says so), and the
# menu-bar path. See docs/updates.md.
#
# Usage: ./tools/update-e2e.sh [scratch-dir]
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# Not named "patchthrough-update-…": that is the shape the installer's janitor
# cleans up, and a scratch tree should not depend on how precisely it matches.
WORK="${1:-${TMPDIR:-/tmp}/patchthrough-e2e}"
IDENTITY="${PATCHTHROUGH_SIGN_IDENTITY:-Developer ID Application: Nico Herrera (U3W37KR29G)}"
PORT="${PATCHTHROUGH_E2E_PORT:-8765}"
# Deliberately not the shipping identifier: the running app must keep working.
BUNDLE_ID="com.nicoherrera.patchthrough.e2e"

say() { printf '\n== %s ==\n' "$1"; }
fail() { printf 'FAIL: %s\n' "$1" >&2; exit 1; }

command -v python3 >/dev/null || fail "python3 is needed to serve the local feed"
security find-identity -v -p codesigning 2>/dev/null | grep -q "Developer ID" \
  || fail "no Developer ID identity in the keychain; the verifier needs a real signature"

rm -rf "$WORK"
mkdir -p "$WORK/feed"
cd "$REPO"

say "building"
swift build >/dev/null
BIN=".build/debug/patchthrough"
[ -x "$BIN" ] || fail "no debug binary at $BIN"

make_bundle() {   # $1 = destination .app, $2 = version
  local app="$1" version="$2"
  rm -rf "$app"
  mkdir -p "$app/Contents/MacOS"
  cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key>       <string>patchthrough</string>
    <key>CFBundleIdentifier</key>       <string>$BUNDLE_ID</string>
    <key>CFBundleName</key>             <string>Patchthrough</string>
    <key>CFBundleShortVersionString</key> <string>$version</string>
    <key>CFBundleVersion</key>          <string>1</string>
    <key>LSMinimumSystemVersion</key>   <string>15.0</string>
    <key>LSUIElement</key>              <true/>
</dict>
</plist>
EOF
  cp "$BIN" "$app/Contents/MacOS/patchthrough"
  codesign --force --sign "$IDENTITY" --timestamp --options runtime \
    --entitlements packaging/patchthrough.entitlements "$app" 2>/dev/null
  codesign --verify --strict "$app" || fail "the $version bundle failed to sign"
}

build_feed() {    # rebuilds the DMG, the sidecar, and the feed document
  rm -rf "$WORK/dmgroot"
  mkdir -p "$WORK/dmgroot"
  cp -R "$WORK/staging/patchthrough.app" "$WORK/dmgroot/"
  ln -s /Applications "$WORK/dmgroot/Applications"
  hdiutil create -volname "Patchthrough" -srcfolder "$WORK/dmgroot" \
    -ov -format UDZO -imagekey zlib-level=9 -quiet "$WORK/feed/Patchthrough-arm64.dmg"
  codesign --force --sign "$IDENTITY" --timestamp "$WORK/feed/Patchthrough-arm64.dmg" 2>/dev/null
  ( cd "$WORK/feed" && shasum -a 256 Patchthrough-arm64.dmg > Patchthrough-arm64.dmg.sha256 )
  cat > "$WORK/feed/latest" <<EOF
{
  "tag_name": "v99.9.9",
  "draft": false,
  "prerelease": false,
  "assets": [
    {"id": 1, "name": "Patchthrough-arm64.dmg",
     "size": $(stat -f%z "$WORK/feed/Patchthrough-arm64.dmg"),
     "browser_download_url": "http://127.0.0.1:$PORT/Patchthrough-arm64.dmg"},
    {"id": 2, "name": "Patchthrough-arm64.dmg.sha256",
     "size": $(stat -f%z "$WORK/feed/Patchthrough-arm64.dmg.sha256"),
     "browser_download_url": "http://127.0.0.1:$PORT/Patchthrough-arm64.dmg.sha256"}
  ]
}
EOF
}

make_bundle "$WORK/installed/patchthrough.app" "1.0.0"
make_bundle "$WORK/staging/patchthrough.app" "99.9.9"
build_feed

( cd "$WORK/feed" && exec python3 -m http.server "$PORT" --bind 127.0.0.1 >/dev/null 2>&1 ) &
SERVER=$!
trap 'kill $SERVER 2>/dev/null || true' EXIT
sleep 1

export PATCHTHROUGH_UPDATE_FEED="http://127.0.0.1:$PORT/latest"
export PATCHTHROUGH_UPDATE_SKIP_GATEKEEPER=1
APP="$WORK/installed/patchthrough.app"
INSTALLED="$APP/Contents/MacOS/patchthrough"
version_of() { /usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$1/Contents/Info.plist"; }

say "1. the check finds the newer release"
"$INSTALLED" update | tee "$WORK/check.out"
grep -q "99.9.9 is available" "$WORK/check.out" || fail "the check did not offer 99.9.9"

say "2. the install runs"
"$INSTALLED" update --install

say "3. the bundle really moved"
[ "$(version_of "$APP")" = "99.9.9" ] || fail "the bundle is still $(version_of "$APP")"
codesign --verify --strict "$APP" || fail "the swapped bundle is not validly signed"
echo "99.9.9, still validly signed"

say "4. the janitor left nothing behind"
leftovers="$(ls -A "$WORK/installed" | grep -v '^patchthrough.app$' || true)"
[ -z "$leftovers" ] || fail "swap leftovers: $leftovers"
echo "clean"

say "5. a second check is a no-op"
"$INSTALLED" update | tee "$WORK/again.out"
grep -q "Up to date (99.9.9)" "$WORK/again.out" || fail "the second check did not report up to date"

say "6. a tampered image is refused"
printf 'x' | dd of="$WORK/feed/Patchthrough-arm64.dmg" bs=1 seek=600000 conv=notrunc 2>/dev/null
make_bundle "$WORK/installed/patchthrough.app" "1.0.0"
if "$INSTALLED" update --install 2>"$WORK/tamper.err"; then
  fail "the tampered image installed"
fi
grep -q "checksum" "$WORK/tamper.err" || fail "the refusal did not name the checksum"
[ "$(version_of "$APP")" = "1.0.0" ] || fail "a refused update still changed the bundle"
echo "refused, and the app is untouched"

say "7. a release that announces the wrong version is refused"
build_feed
python3 - "$WORK/feed/latest" <<'PY'
import json, sys
path = sys.argv[1]
feed = json.load(open(path))
# The image inside still says 99.9.9, so this tag must not be trusted.
feed["tag_name"] = "v99.9.10"
json.dump(feed, open(path, "w"), indent=2)
PY
if "$INSTALLED" update --install 2>"$WORK/mismatch.err"; then
  fail "a tag that disagreed with the bundle installed"
fi
grep -qi "but the release says" "$WORK/mismatch.err" || fail "the refusal did not name the mismatch"
[ "$(version_of "$APP")" = "1.0.0" ] || fail "a refused update still changed the bundle"
echo "refused, and the app is untouched"

printf '\nall checks passed\n'
