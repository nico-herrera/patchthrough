# How Patchthrough updates itself

The app replaces its own bundle from a GitHub release. This file records what it
checks, when it checks, and how to test the path without publishing anything.

## The feed

`UpdateSource.feedRepo` names the repository. The feed is that repository's
`releases/latest` on the GitHub API, read anonymously. Each release carries
`Patchthrough-arm64.dmg` and a `.sha256` sidecar, and `packaging/notarize.sh`
rewrites the sidecar after stapling, so the published hash is the post-staple one.

The DMG name carries no version on purpose: the landing page links straight to
`releases/latest/download/Patchthrough-arm64.dmg`. The version lives in the tag and
in the bundle. **The tag and the bundle version must agree**, because the app
compares them (see below).

## What the app verifies before it replaces anything

In order, in `UpdateVerifier` and `UpdatePipeline`:

1. TLS to the API host. There is no certificate pinning: GitHub rotates
   certificates, and a stale pin would end updates for every install.
2. The release is neither a draft nor a prerelease, and its tag parses as a
   version greater than the running one.
3. The DMG matches the `.sha256` sidecar. This is a weak check, because the hash
   travels the same channel as the image. It still catches truncation and the
   class of operator error where the sidecar was not refreshed after stapling.
4. The image mounts read-only and off Finder, and holds exactly one `.app`.
5. The app's signature is valid under strict rules, chains to Apple's Developer ID
   anchor, and names `UpdateSource.expectedTeamID` as its team.
6. Gatekeeper accepts the app, which is where notarization gets enforced. A debug
   build can skip this step with `PATCHTHROUGH_UPDATE_SKIP_GATEKEEPER=1`, and it
   says so on stderr when it does. A release build has no such path.
7. The downloaded bundle identifier equals the running one. This is what stops the
   public build from installing over the Fusion92 fork, and the reverse.
8. The downloaded bundle version equals the tag the release announced, and exceeds
   the running version. Nothing at release time ties a tag to the bundle inside
   the image, so the client refuses the mismatch instead of trusting the tag.

Only then does the swap happen. `PATCHTHROUGH_UPDATE_FEED` can point the app at
another feed for testing, and it changes only what is offered: every check above
still runs, so the worst a hostile feed can deliver is a genuine, newer, correctly
signed Patchthrough.

## The swap and the restart

`UpdateInstaller.swap` copies the verified app to a hidden staged sibling of the
destination, then renames twice: the old bundle out, the staged one in. It rolls
the first rename back if the second fails, so a failed update always leaves a
working app. Two renames beat `FileManager.replaceItemAt`, whose safe-save
machinery misbehaves on a running bundle. The running process keeps executing from
the renamed-away inodes, and the next launch deletes them.

After the swap the installer runs `lsregister -f`. Without it a replaced bundle can
silently lose its Notification Center registration.

Restarting depends on how the app was started, and only one of the two is a daemon:

- LaunchAgent bootstrapped (`launchctl print gui/<uid>/<label>` exits 0):
  `launchctl kickstart -k`. A clean exit would **not** be respawned, because the
  agent keeps alive only on `SuccessfulExit: false`.
- Otherwise: a detached helper waits for this process to exit, then opens the new
  bundle. It waits because an instance that starts too early sees the old one,
  signals it, and quits.

`kickstart -k` kills without ceremony, so the updater flushes its bookkeeping
before it calls out.

A destination the user cannot write (a drag-installed `/Applications` owned by an
admin) never triggers privilege escalation. The app opens the verified image and
asks for a drag instead.

## When it checks

About every six hours, with a 30-minute timer tolerance and up to 15 minutes of
jitter per fire, so a fleet that starts together does not arrive together. The
first check waits a minute or two after launch, and only when the last check is
more than six hours old. `If-None-Match` means an unchanged feed costs no rate
limit. A check that lands during a recording is skipped and runs when the
recording stops.

Nothing installs while a recording runs. An install requested mid-recording waits,
and the menu says so. A transcription is different: the app may restart under one,
and the new instance re-queues the pending work.

Upstream ships with checks on and a Settings toggle. A build whose `UpdateSource`
sets `allowsDisabling = false` ignores the config key and always checks.

## State

`updates.check` in `~/.config/patchthrough/config.json` is the user's intent, and
is written only when it is false. Everything else is app state in the defaults
domain: `update.lastCheckedAt`, `update.feedETag`, `update.lastOutcome`,
`update.lastOutcomeAt`. `patchthrough doctor` reads the recorded outcome and never
the network, because doctor also runs at every launch.

Doctor fails, rather than warns, on `failed:unauthorized`. On a private feed that
outcome means the credential is dead and updates have stopped arriving, which has
no other visible symptom.

## Testing it

`tools/update-e2e.sh` builds two signed bundles in a scratch directory, serves the
newer one as a DMG over a local HTTP feed, and drives the whole path with the CLI.
It never touches `~/Applications`, and it gives the scratch bundles their own
bundle identifier so a running Patchthrough neither blocks the test nor gets
replaced by it. It covers the checksum, signature, team, identity, swap, janitor,
and refusal cases; it skips the Gatekeeper assessment, because the image it builds
is signed but not notarized.

For the menu-bar path, run a real bundle with `PATCHTHROUGH_DEBUG_UPDATE=1`, which
checks at once instead of waiting out the settle delay and traces what it does to
stderr. A full-fidelity test of the published path needs a notarized DMG on a
scratch repository, and `PATCHTHROUGH_UPDATE_FEED` pointed at it.
