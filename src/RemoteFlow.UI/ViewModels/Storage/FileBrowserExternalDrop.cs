namespace RemoteFlow.UI.ViewModels.Storage;

/// <summary>A drop a <c>FileBrowserPane</c> cannot perform itself, because the rows came from something
/// that is not another pane.
///
/// The dragged-from page supplies the action — it is the only thing that knows what is moving — and the
/// pane supplies the destination directory the pointer was over. That keeps the pane free of any knowledge
/// of SFTP, which is what lets one pane class serve <c>C:\Users\andreas</c>, <c>media-prod/2024/</c> and
/// the local half of the SFTP page without a single source-specific branch.</summary>
/// <param name="Verb">What the drop will do, for the pane's drop-target message: "Download to".</param>
/// <param name="DropAsync">Runs the drop into the destination directory the pane was dropped on.</param>
public sealed record FileBrowserExternalDrop(string Verb, Func<string, CancellationToken, Task> DropAsync);
