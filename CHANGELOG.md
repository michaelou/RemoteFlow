# Changelog

All notable changes to RemoteFlow are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RemoteFlow uses
[Semantic Versioning](https://semver.org/spec/v2.0.0.html). Versions come from git tags: a release is
whatever commit carries a `v`-prefixed tag, so an entry here and a tag are two halves of the same act.

## [Unreleased]

### Added

- Tag-driven versioning with MinVer. Every assembly records the version and the commit it was built from,
  `RemoteFlow.exe --version` prints both, and the Settings page has an About tab showing the same values.
- This changelog.

## [0.0.0] - unreleased

RemoteFlow has not had a tagged release yet. Everything up to this point was built and reviewed as
pre-release work across Milestones 1 to 7: connections and folders, the SSH transport and host key policy,
the embedded terminal, SFTP browsing and transfers, remote editing, credential storage, and the backup
format. The first tagged release will restate what shipped rather than trying to reconstruct that history
here.
