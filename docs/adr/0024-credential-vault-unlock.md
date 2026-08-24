# ADR-0024: Credential vault unlock

- Status: Accepted
- Date: 2026-08-24

## Context

`EncryptedFileVaultProvider` has had `UnlockAsync` and `IsUnlocked` since it was written, and nothing ever
called them. A grep for either outside the provider found only `CryptoAndVaultTests`.

`CredentialProviderSelector` falls back to the file vault whenever the platform's own store is missing —
which on Linux means whenever libsecret cannot be loaded or initialised — and `EncryptedFileVaultProvider`
reports `IsAvailable => true` unconditionally. So the selector handed out a provider that answered every
read and write with `VaultLockedException` from `RequireDocument()`. On such a machine RemoteFlow could not
store or read a credential at all, and nothing said so.

The automatic backup tab surfaced it by being the first UI to read a credential during page load, where the
exception escaped into the view. That was a missing catch, fixed separately. This is the cause underneath.

## Decision

### The vault is opened at startup, before anything reads a credential

`IVaultUnlockService.EnsureUnlockedAsync()` runs from `App.StartupAction`, immediately after the database
and theme are ready and before the sweeps, the auto-backup runner, and the first navigation. `StartupAction`
runs on `MainWindow.Opened`, so there is a visible window to own the dialog.

Unlocking lazily, on the first credential access, was rejected: the access points are inside connection
opens and page loads, several of them on background threads, and each would need its own "am I allowed to
show a modal right now" answer. One prompt at a known moment is easier to reason about and easier to cancel.

### The trigger is a type, not a platform test or a name match

`EnsureUnlockedAsync` asks the selector for the provider and checks `is ICredentialVault`. Providers the
operating system opens as part of signing in — Windows credential manager, macOS keychain, libsecret — do
not implement it, so on those machines the call asks nothing and returns immediately.

Comparing `provider.Name == "file-vault"` would work today and would be wrong the moment a second lockable
store exists. `ICredentialVault` also gives the Application layer somewhere honest to put `Exists` and a
result-returning unlock.

### Unlock reports, it does not throw

`ICredentialVault.TryUnlockAsync` returns `VaultUnlockOutcome`. A wrong passphrase is an ordinary thing for
a person to do and should not travel as an exception — particularly not one declared in the infrastructure
layer, which the coordinator cannot reference. `EncryptedFileVaultProvider.TryUnlockAsync` wraps the existing
`UnlockAsync` and maps `VaultUnlockException` to `IncorrectPassphrase`.

`IncorrectPassphrase` deliberately also covers a corrupt or truncated vault file. Authenticated decryption
cannot distinguish the two, and inventing a distinction would present a guess as a fact.

### Creating and opening are one prompt with two shapes

An absent vault is created by its first unlock, so the first run asks the user to *invent* a passphrase and
the rest ask them to *recall* one. Those are different questions: the first gets a confirmation box, the
`PassphrasePolicy` strength rule, and the warning that nothing else holds a copy; the second gets one box
and, on a retry, the reason the last attempt failed. `VaultUnlockPromptRequest.IsNewVault` carries the
distinction, read once before the loop so the wording cannot change underneath the user.

`PassphrasePolicy` was `BackupPassphrasePolicy` under `Abstractions.Backup`. It now governs three
passphrases — manual export, automatic backup, and the vault — so it moved up to `Abstractions` and lost the
prefix. A strength rule enforced in three places drifts; one read from a single place cannot.

### Declining is an answer

Cancelling returns a `VaultUnlockStatus` with `IsUsable = false` and a sentence explaining what is now
unavailable. RemoteFlow runs; it just cannot remember secrets this session. Startup does not fail, no error
dialog appears, and the Backup page — which already reports an unusable credential store — grows an
**Unlock…** button so changing your mind does not mean restarting.

Three attempts, then the prompt stops for the session. The vault's Argon2id parameters are the real
brute-force defence; the limit only stops an endless dialog loop. A `Failed` outcome — an unreadable file, a
directory that cannot be written — stops immediately, because retyping does not fix it.

`EnsureUnlockedAsync` is serialised behind a semaphore so startup and a page asking at the same moment
produce one dialog, not two asking the same question.

## Consequences

- On Linux without a working keyring, RemoteFlow now asks for a vault passphrase at every launch. That is
  inherent to an encrypted store with no OS-managed key, and it is the price of saving credentials at all on
  such a machine. Windows, macOS, and Linux with libsecret are unaffected and see no dialog.
- The vault passphrase cannot be recovered. It is stated in the creation dialog, where it can still be acted
  on, rather than only in documentation.
- A session where the user declined still works: connections prompt for passwords as they always did when
  nothing was saved, and the Backup page explains why automatic backup is off.
- `ICredentialProvider.IsAvailable` remains a weaker claim than "usable" — the file vault reports available
  while locked. Callers that need a working store must handle failure regardless; see
  [ADR-0023](0023-automatic-backup.md) for how the automatic backup passphrase store does it.
