using RemoteFlow.Domain.Common;

namespace RemoteFlow.Domain.ValueObjects;

public sealed class RdpOptions
{
    private RdpOptions()
    {
        RedirectClipboard = true;
    }

    public string? Domain { get; private set; }

    public bool FullScreen { get; private set; }

    public int? Width { get; private set; }

    public int? Height { get; private set; }

    public bool Multimon { get; private set; }

    public bool RedirectClipboard { get; private set; }

    public bool RedirectDrives { get; private set; }

    public static RdpOptions Default()
    {
        return new();
    }

    public Result<RdpOptions> Configure(
        string? domain = null,
        bool fullScreen = false,
        int? width = null,
        int? height = null,
        bool multimon = false,
        bool redirectClipboard = true,
        bool redirectDrives = false)
    {
        if (width is <= 0 || height is <= 0)
        {
            return Result<RdpOptions>.Failure(RemoteFlowError.Validation(
                "rdp.dimensions",
                "RDP dimensions must be greater than zero when specified."));
        }

        Domain = string.IsNullOrWhiteSpace(domain) ? null : domain.Trim();
        FullScreen = fullScreen;
        Width = width;
        Height = height;
        Multimon = multimon;
        RedirectClipboard = redirectClipboard;
        RedirectDrives = redirectDrives;
        return Result<RdpOptions>.Success(this);
    }
}
