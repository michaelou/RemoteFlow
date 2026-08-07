namespace RemoteFlow.Application.Abstractions;

public interface IErrorDialogService
{
    Task ShowAsync(string title, string message, CancellationToken cancellationToken = default);
}

public interface IGlobalExceptionHandler
{
    void Install();

    Task HandleAsync(
        Exception exception,
        string context,
        bool isTerminating = false,
        CancellationToken cancellationToken = default);
}
