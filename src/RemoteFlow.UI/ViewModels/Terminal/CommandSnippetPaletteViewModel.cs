using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RemoteFlow.Application.Services;

namespace RemoteFlow.UI.ViewModels.Terminal;

/// <summary>One command as the palette shows it: what it does, what it costs, and whether it still has a
/// hole to fill before it can be run.</summary>
public sealed class CommandSnippetItemViewModel(CommandSnippet snippet)
{
    public CommandSnippet Snippet { get; } = snippet;

    public string Title => Snippet.Title;

    public string Command => Snippet.Command;

    public string Description => Snippet.Description;

    public string GroupName => Snippet.GroupName;

    public bool HasRiskBadge => Snippet.Risk != CommandRisk.Safe;

    public string RiskLabel => Snippet.Risk switch
    {
        CommandRisk.Warning => "Careful",
        CommandRisk.Danger => "Destructive",
        CommandRisk.Safe => string.Empty,
        _ => string.Empty,
    };

    /// <summary>Whether the badge is the red one rather than the amber one.</summary>
    public bool IsDestructive => Snippet.Risk == CommandRisk.Danger;

    public bool HasPlaceholder => Snippet.HasPlaceholder;
}

/// <summary>
/// The searchable list of commands offered at a prompt. Choosing one types it at the cursor; running it
/// stays the user's Enter, which is what makes it safe to offer a command with a placeholder in it.
/// </summary>
public sealed partial class CommandSnippetPaletteViewModel(CommandSnippetLibrary library) : ObservableObject
{
    private readonly CommandSnippetLibrary _library = library ?? throw new ArgumentNullException(nameof(library));

    public CommandSnippetPaletteViewModel()
        : this(new CommandSnippetLibrary())
    {
    }

    public ObservableCollection<CommandSnippetItemViewModel> Results { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; private set; }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial CommandSnippetItemViewModel? SelectedResult { get; set; }

    [ObservableProperty]
    public partial bool HasEmptyState { get; private set; }

    [ObservableProperty]
    public partial string EmptyMessage { get; private set; } = string.Empty;

    /// <summary>Opens on the whole library rather than on an empty box: this is a list to browse when the
    /// command you want is the one you cannot remember the name of.</summary>
    public void Open()
    {
        SearchText = string.Empty;
        Populate(string.Empty);
        IsOpen = true;
    }

    public void Close()
    {
        IsOpen = false;
    }

    /// <summary>Moves the highlight by <paramref name="delta" />, stopping at either end.</summary>
    public void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var current = SelectedResult is null ? -1 : Results.IndexOf(SelectedResult);
        var next = Math.Clamp(current + delta, 0, Results.Count - 1);
        SelectedResult = Results[next];
    }

    /// <summary>Takes the highlighted command and closes. Returns <see langword="null" /> when nothing is
    /// highlighted, so a stray Enter over an empty search does nothing at all.</summary>
    public CommandSnippetItemViewModel? Commit()
    {
        var chosen = SelectedResult;
        if (chosen is null)
        {
            return null;
        }

        Close();
        return chosen;
    }

    partial void OnSearchTextChanged(string value)
    {
        Populate(value);
    }

    private void Populate(string text)
    {
        Results.Clear();
        foreach (var snippet in _library.Search(text))
        {
            Results.Add(new CommandSnippetItemViewModel(snippet));
        }

        SelectedResult = Results.FirstOrDefault();
        HasEmptyState = Results.Count == 0;
        EmptyMessage = !HasEmptyState
            ? string.Empty
            : _library.LoadError ?? (string.IsNullOrWhiteSpace(text)
                ? "The command library is empty."
                : $"No commands match “{text.Trim()}”. Try a tool name, a tag, or part of the command.");
    }
}
