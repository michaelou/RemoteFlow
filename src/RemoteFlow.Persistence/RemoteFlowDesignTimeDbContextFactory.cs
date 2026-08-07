using Microsoft.EntityFrameworkCore.Design;

namespace RemoteFlow.Persistence;

public sealed class RemoteFlowDesignTimeDbContextFactory : IDesignTimeDbContextFactory<RemoteFlowDbContext>
{
    public RemoteFlowDbContext CreateDbContext(string[] args)
    {
        var dataDirectory = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : Directory.GetCurrentDirectory();

        return new RemoteFlowDbContext(RemoteFlowDatabase.CreateOptions(dataDirectory));
    }
}
