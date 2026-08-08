# Third-party licences

RemoteFlow is MIT and ships around eighty third-party packages inside its binaries. Two things have to
stay true, and neither can rely on anyone remembering:

1. Every package a user runs is attributed, with its licence, in `THIRD-PARTY-NOTICES.md`.
2. No package arrives under a licence incompatible with shipping it inside an MIT application.

The second is the one worth automating. Nobody adds a copyleft dependency on purpose; one arrives
transitively, four levels down, in a routine version bump. Discovering that by reading the licences of a
released binary is discovering it far too late.

## How it works

`scripts/generate-notices.ps1` reads what NuGet actually resolved for `src/RemoteFlow.Desktop` —
transitive packages included, because those are the ones nobody chose — reads each package's `.nuspec`,
and writes two files from one pass, so they cannot disagree:

| File | Audience |
| --- | --- |
| `THIRD-PARTY-NOTICES.md` | People. Every shipping package with its version, licence, and copyright, followed by the full text of each licence named. |
| `build/licenses/package-licenses.txt` | The build. A flat `id\|version\|spdx` manifest. |

Regenerate both after any dependency change and commit the result:

```shell
pwsh ./scripts/generate-notices.ps1
```

The output is deterministic — sorted, no timestamps, LF endings — so running it twice produces no diff.

### The build-time check

`build/licenses/PackageLicenses.targets` runs on every build of the desktop application and makes two
assertions:

- Every entry in the manifest carries a licence listed in `build/licenses/allowed-licenses.txt`
  (**RF0001**).
- Every package this build resolved appears in the manifest (**RF0002**).

The second keeps the first honest. Without it a brand new dependency would simply be absent from the
manifest and checked against nothing.

Both are `dotnet build` errors, not warnings. A binary whose contents are not understood should not be
produced.

### The CI check

The build sees the manifest; it cannot see whether `THIRD-PARTY-NOTICES.md` still matches. CI runs
`generate-notices.ps1 -Verify`, which regenerates into memory and compares, so a pull request that adds a
dependency without regenerating the notices fails there.

## The policy files

| File | What it is |
| --- | --- |
| `allowed-licenses.txt` | SPDX identifiers RemoteFlow will ship. Permissive only. |
| `license-overrides.txt` | Packages whose `.nuspec` carries no SPDX expression, with the answer a human read out of the bundled licence file, and where they read it. |
| `build-only-packages.txt` | Packages that take part in the build but ship nothing. Their licences are still checked; they stay out of the user-facing notices. |
| `texts/<spdx>.txt` | The full text of each licence, reproduced in the notices. |

## Adding a dependency

1. Add the `PackageReference` and the version in `Directory.Packages.props` as usual.
2. `pwsh ./scripts/generate-notices.ps1`.
3. If it fails because the licence cannot be identified, open the package folder under
   `~/.nuget/packages`, read the licence it ships, and record the answer in `license-overrides.txt` with a
   comment saying where you read it. **Do not guess** — that is the one step this whole arrangement exists
   to prevent someone skipping.
4. If it fails because the licence is not on the allow-list, that is a decision, not a formality. Either
   drop the dependency, or accept the licence deliberately: add it to `allowed-licenses.txt` with a
   reason, add `texts/<identifier>.txt`, and say so in the pull request.
5. Commit `THIRD-PARTY-NOTICES.md` and `build/licenses/package-licenses.txt` with the change.

## Where users see it

The about box, under **Settings → About**, shows the notices inline. They are embedded in
`RemoteFlow.UI.dll` rather than read from a file beside the executable, because attribution has to survive
packaging: someone who extracted a portable zip has the binary and nothing else.

## Verifying the check actually fails

Worth doing once after touching any of this — a gate nobody has watched fail is not known to be a gate:

```shell
"Evil.Copyleft.Package|1.0.0|GPL-3.0-only" | Add-Content build/licenses/package-licenses.txt
dotnet build src/RemoteFlow.Desktop
```

That must fail with **RF0001**. Revert the line afterwards.
