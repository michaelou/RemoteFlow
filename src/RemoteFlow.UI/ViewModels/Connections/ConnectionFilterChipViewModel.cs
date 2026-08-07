using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Domain.Enums;

namespace RemoteFlow.UI.ViewModels.Connections;

public sealed partial class ConnectionFilterChipViewModel : ObservableObject
{
    private ConnectionFilterChipViewModel(
        string label,
        ProtocolType? protocol = null,
        EnvironmentKind? environment = null,
        Guid? tagId = null)
    {
        Label = label;
        Protocol = protocol;
        Environment = environment;
        TagId = tagId;
    }

    public event EventHandler? SelectionChanged;

    public string Label { get; }

    public ProtocolType? Protocol { get; }

    public EnvironmentKind? Environment { get; }

    public Guid? TagId { get; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    public static ConnectionFilterChipViewModel ForProtocol(ProtocolType protocol)
    {
        return new ConnectionFilterChipViewModel(protocol.ToString().ToUpperInvariant(), protocol: protocol);
    }

    public static ConnectionFilterChipViewModel ForEnvironment(EnvironmentKind environment)
    {
        var label = environment == EnvironmentKind.Development ? "DEV" : environment.ToString().ToUpperInvariant();
        return new ConnectionFilterChipViewModel(label, environment: environment);
    }

    public static ConnectionFilterChipViewModel ForTag(Guid tagId, string name)
    {
        return new ConnectionFilterChipViewModel(name, tagId: tagId);
    }

    partial void OnIsSelectedChanged(bool value)
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }
}
