# ADR-0018: Self-update by launching the installer

- Status: Accepted
- Date: 2026-08-11
- Supersedes the "no updater" half of [ADR-0016](0016-update-check.md)

## Context

[ADR-0016](0016-update-check.md) added a check and refused an updater, on the grounds that code signing was
the precondition: "with no code-signing certificate every downloaded installer meets a SmartScreen warning
anyway". There is still no certificate. What changed is that two of that ADR's premises turned out to be
wrong on inspection, and a third obstacle turned out to be already solved.

**The SmartScreen premise describes a different flow.** That warning comes from the Attachment Execution
Service, which reads the Mark-of-the-Web alternate data stream a browser writes onto a download and which
`ShellExecute` consults. A file written by `HttpClient` to a `FileStream` acquires no such stream, and
`Process.Start` with `UseShellExecute = false` goes through `CreateProcess`, which does not consult the
service at all. So the dialog ADR-0016 was reasoning about does not appear on this path. RemoteFlow is not
evading a check; it is downloading in a way that never acquires one — which is precisely why something has
to be put in its place.

**`checksums.txt` was already there.** Every release publishes one, and ADR-0016 treated it as something
the user could check by hand. It is equally something the application can check, and unlike a reputation
signal it is one RemoteFlow can make mandatory.

**The two-distribution-shapes problem dissolves by refusing one of them.** ADR-0016 worried about a
portable build replacing an executable it is running from. It does not have to: RemoteFlow can tell which
shape it is and offer the button only to the one that can be upgraded safely.

The remaining argument for doing this at all is that the status quo is not "no risk". The About tab already
links to a release page from which the user downloads and runs the same unsigned installer, usually without
checking any hash. The in-app path is strictly narrower than the one already on offer.

## Decision

Add an install button beside the existing check. It is a second, separate press, and it opens a dialog
before anything is downloaded.

- **Only an installed copy may update itself.** `IAppInstallInfo` reads
  `HKCU\...\Uninstall\{AppId}_is1\Inno Setup: App Path` — the same value Inno Setup reads when deciding
  where an upgrade goes — and requires it to name the directory the running executable is in. A portable
  zip, a copy that has been moved, a build output directory, and every non-Windows build are all refused,
  each with a sentence saying why, shown where the button would have been.
- **SHA-256 is mandatory and has no override.** `checksums.txt` is fetched *before* the installer, so a
  release that could never be verified costs one small request. The download is hashed as it is written,
  and lands under a `.partial` name that is only renamed once the digest matches. There is no "install
  anyway": ADR-0016's objection was that running an unsigned downloaded binary is the risky part, so the
  published digest is the compensating control, and a control with a bypass is not one.
- **Every URL is checked before it is contacted.** Three rules in `GitHubReleaseUris`, of deliberately
  different strictness. A release *page* is handed to a browser, so any `https` subdomain of `github.com`
  will do — that is ADR-0016's existing rule, unchanged. An *asset* is downloaded and then executed, so it
  must be `https` on exactly `github.com` under this repository's own `releases/download/` path. Redirects
  are followed by hand with `AllowAutoRedirect = false`, each hop checked before the request rather than
  after, because automatic redirects would have already contacted the wrong host by the time anything could
  look — and that also keeps GitHub's signed storage token from being sent somewhere it was not issued for.
- **The application exits before the installer starts.** Pressing the button downloads, verifies, and then
  only *queues* the installer; `Program.Main` runs it from its `finally`, after the window has closed and
  the host has stopped. Starting the installer first and then trying to leave would race it against the
  process whose files it is replacing.
- **`AppMutex`, released just before the launch.** RemoteFlow now holds a named mutex for its lifetime, so
  Setup and — for the first time — the uninstaller stop and ask rather than replacing files underneath a
  running copy. The updater drops the handle immediately before starting Setup, so the race is designed
  away rather than gambled on.
- **`/SILENT`, not `/VERYSILENT`.** The user has just watched RemoteFlow disappear and will not see it
  again for ten seconds or so. A progress window is the honest representation of that; an empty screen
  reads as a crash. The two are identical as far as `skipifsilent` is concerned, so this costs the relaunch
  nothing.
- **A custom `/UPDATE` flag drives the relaunch.** Setup passes parameters it does not recognise through to
  `[Code]`, which is how `/PURGEDATA` already works. A second `[Run]` entry, flagged `skipifnotsilent` and
  gated on `Check: RelaunchAfterUpdateRequested`, starts RemoteFlow again. The interactive install keeps its
  checkbox; a CI smoke test or an unattended deployment gets neither.
- **No new setting.** The button plus the confirmation dialog is the consent, and it is a better one than a
  checkbox: it states the version, the size, what is verified, what is not, and that the application will
  close and reopen — at the moment of the decision, rather than in a settings page read once and forgotten.

Deliberately **not** passed to Setup, each for a reason: `/DIR`, because Inno reads its own uninstall entry
and gets it right in every case ours would plus one where ours would not; `/TASKS`, because
`UsePreviousTasks` restores the desktop-icon choice the user actually made; `/SUPPRESSMSGBOXES`, because by
then RemoteFlow has exited and a suppressed message is a silent failure with nobody left to report it — and
suppressed answers default to Cancel.

## Consequences

**The trust boundary is TLS to `github.com`, not the checksum.** Worth stating plainly rather than letting
the word "verified" do work it cannot. `checksums.txt` is unsigned and served from the same host as the
installer, so matching it proves the download arrived intact and unmodified in transit; it does not prove
who built it. Anyone who can publish to this repository is not stopped by this — and neither are they
stopped by a user downloading the same file in a browser. What the in-app path adds is a hash check almost
nobody performs by hand. What it removes is a SmartScreen prompt that, as established above, this flow was
never going to show. Signing remains worth doing, and remains undone; this ADR defers that decision rather
than resolving it.

**A failed install can leave no application at all.** Inno Setup rolls back by uninstalling what the run
installed, not by restoring what it replaced, so a failure two-thirds of the way through an upgrade leaves
the old files it never reached and nothing where the new ones were — possibly including `RemoteFlow.exe`.
Nothing is running to notice. Two mitigations, both load-bearing: the downloaded installer is kept and its
path is named on screen before the application exits, and a `pending.json` marker is written beforehand so
the next launch — from the Start-menu shortcut, which survives — can say what happened and name the Inno
log in RemoteFlow's own log folder.

**The release asset names and `checksums.txt` are now a parsed contract.** `RemoteFlow-<version>-<rid>-setup.exe`
and the two-space `sha256sum` format are read by code, so renaming an asset or changing that format would
silently disable self-update for everyone already running an older build. The release workflow now asserts
both, and [releasing.md](../releasing.md) says so.

**An emulated x64 build on an Arm machine stays on the x64 track.** Asset selection reads the runtime
identifier the running build was published for, not the machine's. Arguably such a user should be moved to
the native build — but not silently, by an update they did not read.

**The check itself is unchanged.** It is still one unauthenticated GET, still opt-in, still downloads
nothing. Reading `assets[]` costs no extra request because they arrive in the same response.
