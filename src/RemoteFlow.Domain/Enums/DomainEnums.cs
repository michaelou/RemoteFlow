namespace RemoteFlow.Domain.Enums;

public enum ProtocolType
{
    Ssh = 1,
    Sftp = 2,
    Rdp = 3,
}

public enum AuthMethod
{
    None = 0,
    Password = 1,
    PrivateKey = 2,
    Agent = 3,
    Certificate = 4,
    KeyboardInteractive = 5,
    Kerberos = 6,
}

public enum EnvironmentKind
{
    Unspecified = 0,
    Development = 1,
    Staging = 2,
    Production = 3,
}

public enum HostKeyPolicy
{
    Strict = 0,
    TrustOnFirstUse = 1,
    AcceptAny = 2,
}

public enum HostKeyTrust
{
    Trusted = 1,
    Revoked = 2,
}

public enum HostKeySource
{
    UserAccepted = 1,
    ImportedKnownHosts = 2,
    Pinned = 3,
    AcceptAny = 4,
    AlgorithmRotation = 5,
}

public enum CredentialKind
{
    None = 0,
    Password = 1,
    PrivateKeyPassphrase = 2,
    RdpPassword = 3,
}

public enum TerminalKind
{
    Local = 1,
    Ssh = 2,
}

public enum SessionState
{
    Created = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Disconnected = 4,
    Failed = 5,
    Closed = 6,
}

public enum ConflictResolution
{
    Overwrite = 0,
    KeepBoth = 1,
    Discard = 2,
    Cancel = 3,
}

public enum MergeStrategy
{
    Merge = 1,
    Replace = 2,
}

public enum MergeConflictPolicy
{
    PreferLocal = 1,
    PreferImported = 2,
    RenameImported = 3,
}

public enum RemoteFlowErrorKind
{
    Validation = 1,
    AuthenticationRejected = 2,
    HostKeyMismatch = 3,
    RemoteConflict = 4,
    PermissionDenied = 5,
    NotFound = 6,
    ConcurrencyConflict = 7,
    Cancelled = 8,
    Unavailable = 9,
}
