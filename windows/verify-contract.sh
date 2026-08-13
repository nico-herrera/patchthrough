#!/bin/sh
# Prove that the C# recorder writes a session the npm CLI accepts. That is the
# definition of done for the first Windows milestone, and this script checks it
# on any platform, including a Mac.
#
# It writes a session with Patchthrough.Core, then runs the CLI in this
# repository against it, and asserts what the CLI reports.

set -eu

REPO="$(cd "$(dirname "$0")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

ok()   { printf '  \033[32m✓\033[0m %s\n' "$*"; }
fail() { printf '  \033[31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

printf '\033[1mgenerating a session with Patchthrough.Core…\033[0m\n'
SESSION="$(dotnet run --project "$REPO/windows/tools/SessionFixture" -c Release -v quiet -- "$WORK/Recordings")"
NAME="$(basename "$SESSION")"
ok "wrote $NAME"

for file in meta.json transcript.json transcript.raw.json transcript.md handoff.md; do
  [ -f "$SESSION/$file" ] || fail "no $file"
done
ok "the session holds every file of the public contract"

printf '\033[1mreading it back with the npm CLI…\033[0m\n'
CLI="$REPO/cli/bin/patchthrough.js"

# `transcripts` parses meta.json and transcript.md, and prints the first line.
LIST="$(node "$CLI" transcripts --recordings-dir "$WORK/Recordings")"
echo "$LIST" | grep -q "$NAME" || fail "the CLI does not list the session"
echo "$LIST" | grep -q '1m32s' || fail "the CLI reads the wrong duration"
ok 'transcripts: the session is listed as ready, 1m32s'

# `hand --no-launch` stages handoff.md into a repository and prints the prompt.
mkdir -p "$WORK/repo"
STAGED="$(node "$CLI" hand --no-launch --recordings-dir "$WORK/Recordings" --dir "$WORK/repo" 2>&1 >/dev/null)"
echo "$STAGED" | grep -q 'words)' || fail "the CLI did not stage the session"
ok "hand: $(echo "$STAGED" | sed 's/^staged //')"

# The CLI prefers the recorder's handoff.md over rebuilding one, so a byte
# difference here means the recorder wrote a document the CLI would not use.
if ! cmp -s "$SESSION/handoff.md" "$WORK/repo/.meeting/$NAME.md"; then
  fail 'the staged file differs from the handoff.md the recorder wrote'
fi
ok 'the CLI staged the recorder handoff.md verbatim'

# The notes section is prose shared by this recorder, the macOS app and the CLI.
# The staged-file comparison above already proves byte equality with the CLI's own
# rendering; these assert the two rules that are easy to break silently.
grep -q '^- \*\*\[0:09\]\*\* Windows recorder ships before the installer\.$' "$SESSION/handoff.md" \
  || fail 'the notes section is missing or its timestamps do not match the transcript clock'
grep -q '^## Notes$' "$SESSION/handoff.md" || fail 'the notes heading is missing'
grep -q 'My own notes are above the transcript' "$SESSION/handoff.md" \
  || fail 'the instructions did not gain the notes clause'
ok 'notes render above the transcript, on the transcript clock'

# The check above proves the CLI *uses* this document. It does not prove the CLI
# would *write* the same one, because the CLI prefers an existing handoff.md over
# rebuilding. The notes section is prose duplicated in three implementations, so
# rebuild it with the CLI from the same session and compare byte for byte. A
# wording drift in either renderer fails here.
cp "$SESSION/handoff.md" "$WORK/recorder-handoff.md"
rm "$SESSION/handoff.md"
rm -rf "$WORK/repo-rebuilt" && mkdir -p "$WORK/repo-rebuilt"
node "$CLI" hand --no-launch --recordings-dir "$WORK/Recordings" --dir "$WORK/repo-rebuilt" >/dev/null 2>&1 \
  || fail 'the CLI could not rebuild the handoff document'
REBUILT="$WORK/repo-rebuilt/.meeting/$NAME.md"

# One wording is deliberately different, and it is the only one allowed to be. The
# CLI's copies name the Mac, which is false in a document this recorder wrote, so
# the Windows text says "this machine" instead. Both halves are asserted, so a
# change to either renderer shows up here rather than drifting quietly.
grep -q 'the audio this machine played' "$WORK/recorder-handoff.md" \
  || fail 'the recorder no longer says "this machine" in the speakers line'
grep -q 'the audio the Mac played' "$REBUILT" \
  || fail 'the CLI no longer says "the Mac" in the speakers line, so this exception can go'
sed 's/the audio the Mac played/the audio this machine played/' "$REBUILT" > "$WORK/rebuilt-normalized.md"

if ! cmp -s "$WORK/recorder-handoff.md" "$WORK/rebuilt-normalized.md"; then
  diff "$WORK/recorder-handoff.md" "$WORK/rebuilt-normalized.md" | head -20
  fail 'the recorder and the CLI render the handoff document differently'
fi
cp "$WORK/recorder-handoff.md" "$SESSION/handoff.md"
ok 'the recorder and the CLI render identical handoff documents, notes included'

grep -q '^\*\*\[0:01\] me:\*\*' "$SESSION/transcript.md" || fail 'the mic track is not labelled me'
grep -q '^\*\*\[0:05\] them:\*\*' "$SESSION/transcript.md" || fail 'the system track offset was not applied'
ok 'both tracks share one clock, with me and them labels'

printf '\033[1mthe session contract holds end to end\033[0m\n'
