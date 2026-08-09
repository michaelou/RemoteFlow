# ADR-0016: An opt-in update check, and no auto-updater

- Status: Accepted
- Date: 2026-08-09

## Context

Until now RemoteFlow made no network request the user had not configured, and said so in the README, the
About tab, and [releasing.md](../releasing.md). That is a good property to have and a bad one to lose by
accident.

It also left a real gap. Releases are unsigned, distributed as a zip and a per-user installer, and there
is no mechanism by which anyone learns that a new one exists. Someone running 0.1.0 stays on 0.1.0,
including through a security fix, unless they think to look at the repository.

The obvious answer — download and install the update — is worse than it looks here. The two distribution
shapes need different code paths, a portable build has to replace an executable it is running from, and
with no code-signing certificate every downloaded installer meets a SmartScreen warning anyway
(see [packaging-windows.md](../packaging-windows.md)). A silent background updater is not achievable, and
a loud one is just a slower version of opening the release page.

## Decision

Add a check. Do not add an updater.

- **The check reads a version number.** One HTTPS GET to `api.github.com` for this repository's
  `releases/latest`, returning `tag_name` and `html_url`. `SemanticVersion` compares the tag with the
  running build. Nothing is downloaded, verified, extracted, or executed.
- **It runs only when asked.** The button on the About tab runs one check. `SettingKeys.CheckForUpdates`,
  off by default, runs one more per launch. There is no timer, no retry, and no queue.
- **Failures are values, not exceptions.** `UpdateCheckResult` distinguishes up-to-date, an available
  update, a project with no releases, and a failure that carries a sentence to put on screen.
- **A URL from the response is not trusted to be a link.** `html_url` arrives over the network and would
  otherwise be handed to the desktop shell; it is accepted only when it is `https` on `github.com`, and
  falls back to the compiled-in releases page.
- **The claim in the documentation changes with the code.** "Nothing is ever sent anywhere" became a
  description of exactly what the one request contains and when it happens.

`releases/latest` is used rather than the release list because it excludes drafts and prereleases at the
source: a release candidate is then never offered to someone running a stable build, without RemoteFlow
having to decide what a prerelease means.

## Consequences

A user who wants to know about updates can, and a user who wants a program that never contacts anything
still has one — which is the property the earlier position was protecting, and it survives.

The check constrains release tags: a tag has to be a version `SemanticVersion` can parse, so `nightly`
would produce "cannot be compared" rather than a wrong answer. That is recorded in
[releasing.md](../releasing.md).

A 404 from the API is reported as "no releases yet", which is also what a renamed or deleted repository
would produce. Distinguishing them costs a second request to answer a question that only arises if this
project's own URL changes, and the fallback text names the releases page either way.

Self-updating stays out. Reopening it is a decision about code signing first and code second: with a
certificate and a few releases of reputation the download path becomes worth building, and until then
every install is a deliberate act that leaves the user able to check `checksums.txt`.
