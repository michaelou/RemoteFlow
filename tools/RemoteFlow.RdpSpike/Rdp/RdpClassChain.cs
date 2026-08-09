using System.Runtime.InteropServices;
using RemoteFlow.RdpSpike.Interop;

namespace RemoteFlow.RdpSpike.Rdp;

/// <summary>One candidate activation class from the MSTSCLib type library.</summary>
/// <param name="Generation">The MsRdpClient generation, as in MsRdpClient11NotSafeForScripting.</param>
/// <param name="ProgId">The registered ProgID, which lags the generation by one.</param>
/// <param name="ClassId">The CLSID to activate.</param>
internal sealed record RdpClassCandidate(int Generation, string ProgId, Guid ClassId)
{
    public string CoClassName => $"MsRdpClient{Generation}NotSafeForScripting";
}

/// <summary>The outcome of trying to activate one candidate.</summary>
internal sealed record RdpClassProbe(
    RdpClassCandidate Candidate,
    int CoCreateResult,
    bool SupportsClient10,
    bool SupportsOleObject,
    bool SupportsNonScriptable5,
    bool SupportsExtendedSettings,
    string? ControlVersion)
{
    public bool Activated => CoCreateResult == Ole.S_OK;

    public string Summary =>
        $"{Candidate.CoClassName,-32} {(Activated ? "activated" : $"hr=0x{CoCreateResult:X8}")}" +
        (Activated
            ? $"  IMsRdpClient10={Yes(SupportsClient10)} IOleObject={Yes(SupportsOleObject)}" +
              $" NonScriptable5={Yes(SupportsNonScriptable5)} ExtendedSettings={Yes(SupportsExtendedSettings)}" +
              $" version={ControlVersion ?? "?"}"
            : string.Empty);

    private static string Yes(bool value)
    {
        return value ? "yes" : "no";
    }
}

/// <summary>The documented fallback chain, newest class first. The class is what gates the feature set;
/// every generation lives in the same `mstscax.dll`, so a missing class means an older Windows, not a
/// missing install.</summary>
internal static class RdpClassChain
{
    public static IReadOnlyList<RdpClassCandidate> Candidates { get; } =
    [
        new(12, "MsTscAx.MsTscAx.13", new Guid("3f859aa3-c2d4-4faa-b0e4-fd0c9c4e5e3a")),
        new(11, "MsTscAx.MsTscAx.12", new Guid("1df7c823-b2d4-4b54-975a-f2ac5d7cf8b8")),
        new(10, "MsTscAx.MsTscAx.11", new Guid("a0c63c30-f08d-4ab4-907c-34905d770c7d")),
        new(9, "MsTscAx.MsTscAx.10", new Guid("8b918b82-7985-4c24-89df-c33ad2bbfbcd")),
        new(8, "MsTscAx.MsTscAx.9", new Guid("a3bc03a0-041d-42e3-ad22-882b7865c9c5")),
    ];

    /// <summary>Activates each candidate in turn and releases it again, so the log records what this
    /// machine actually offers rather than what the chain hopes for.</summary>
    public static IReadOnlyList<RdpClassProbe> ProbeAll()
    {
        return [.. Candidates.Select(Probe)];
    }

    public static RdpClassProbe Probe(RdpClassCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var unknown = new Guid("00000000-0000-0000-C000-000000000046");
        var hr = Win32.CoCreateInstance(candidate.ClassId, IntPtr.Zero, Win32.CLSCTX_INPROC_SERVER, unknown, out var pUnk);
        if (hr != Ole.S_OK || pUnk == IntPtr.Zero)
        {
            return new RdpClassProbe(candidate, hr, false, false, false, false, null);
        }

        try
        {
            var instance = Marshal.GetObjectForIUnknown(pUnk);
            try
            {
                var client = instance as IMsRdpClient10;
                string? version = null;
                if (client is not null && client.get_Version(out var reported) == Ole.S_OK)
                {
                    version = reported;
                }

                return new RdpClassProbe(
                    candidate,
                    hr,
                    client is not null,
                    instance is IOleObject,
                    instance is IMsRdpClientNonScriptable5,
                    instance is IMsRdpExtendedSettings,
                    version);
            }
            finally
            {
                _ = Marshal.ReleaseComObject(instance);
            }
        }
        finally
        {
            _ = Marshal.Release(pUnk);
        }
    }

    /// <summary>Resolves the class to use: the newest that activates and offers IMsRdpClient10, or the
    /// newest that activates at all when none of them do.</summary>
    public static RdpClassProbe? Resolve(IReadOnlyList<RdpClassProbe> probes, int? pinnedGeneration)
    {
        ArgumentNullException.ThrowIfNull(probes);
        return pinnedGeneration is int pinned
            ? probes.FirstOrDefault(probe => probe.Candidate.Generation == pinned && probe.Activated)
            : probes.FirstOrDefault(probe => probe is { Activated: true, SupportsClient10: true })
                ?? probes.FirstOrDefault(probe => probe.Activated);
    }
}
