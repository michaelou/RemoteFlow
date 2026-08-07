using RemoteFlow.Domain.Abstractions;
using RemoteFlow.Domain.Entities;
using Xunit;

namespace RemoteFlow.Domain.Tests;

public sealed class FolderTests
{
    [Fact]
    public void CreateBuildsTopLevelMaterializedPath()
    {
        var folder = Create("Prod");

        Assert.Null(folder.ParentId);
        Assert.Equal("/Prod", folder.Path);
        Assert.Equal(0, folder.Depth);
    }

    [Fact]
    public void CreateBuildsNestedMaterializedPath()
    {
        var parent = Create("Prod");
        var child = Create("EU", parent);

        Assert.Equal(parent.Id, child.ParentId);
        Assert.Equal("/Prod/EU", child.Path);
        Assert.Equal(1, child.Depth);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a/b")]
    public void CreateRejectsInvalidName(string? name)
    {
        var result = Folder.Create(GuidProvider(), name);

        Assert.True(result.IsFailure);
        Assert.Equal("folder.name", result.Error.Code);
    }

    [Fact]
    public void CreateRejectsCaseInsensitiveSiblingCollision()
    {
        var existing = Create("Prod");

        var result = Folder.Create(GuidProvider(), "prod", existingFolders: [existing]);

        Assert.True(result.IsFailure);
        Assert.Equal("folder.name_collision", result.Error.Code);
    }

    [Fact]
    public void SameNameIsAllowedUnderDifferentParents()
    {
        var left = Create("Left");
        var right = Create("Right");
        var existing = Create("Database", left);

        var result = Folder.Create(GuidProvider(), "Database", right, [left, right, existing]);

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public void MoveToRejectsSelfAsParent()
    {
        var folder = Create("Prod");

        var result = folder.MoveTo(folder, [folder], GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("folder.cycle", result.Error.Code);
    }

    [Fact]
    public void MoveToRejectsDescendantAsParent()
    {
        var root = Create("Prod");
        var child = Create("EU", root);
        var grandchild = Create("Database", child);

        var result = root.MoveTo(grandchild, [root, child, grandchild], GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("folder.cycle", result.Error.Code);
        Assert.Equal("/Prod", root.Path);
    }

    [Fact]
    public void MoveToRejectsSiblingNameCollision()
    {
        var sourceParent = Create("Source");
        var targetParent = Create("Target");
        var moving = Create("Database", sourceParent);
        var collision = Create("database", targetParent);
        Folder[] tree = [sourceParent, targetParent, moving, collision];

        var result = moving.MoveTo(targetParent, tree, GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("folder.name_collision", result.Error.Code);
        Assert.Equal(sourceParent.Id, moving.ParentId);
    }

    [Fact]
    public void MoveToUpdatesEntireSubtreePathAndDepth()
    {
        var prod = Create("Prod");
        var staging = Create("Staging");
        var eu = Create("EU", prod);
        var database = Create("Database", eu);
        var oldDescendantStamp = database.ConcurrencyStamp;
        Folder[] tree = [prod, staging, eu, database];

        var result = eu.MoveTo(staging, tree, GuidProvider());

        Assert.True(result.IsSuccess);
        Assert.Equal(staging.Id, eu.ParentId);
        Assert.Equal("/Staging/EU", eu.Path);
        Assert.Equal(1, eu.Depth);
        Assert.Equal("/Staging/EU/Database", database.Path);
        Assert.Equal(2, database.Depth);
        Assert.NotEqual(oldDescendantStamp, database.ConcurrencyStamp);
    }

    [Fact]
    public void MoveToRootUpdatesEntireSubtreePathAndDepth()
    {
        var prod = Create("Prod");
        var eu = Create("EU", prod);
        var database = Create("Database", eu);
        Folder[] tree = [prod, eu, database];

        _ = eu.MoveTo(null, tree, GuidProvider());

        Assert.Null(eu.ParentId);
        Assert.Equal("/EU", eu.Path);
        Assert.Equal(0, eu.Depth);
        Assert.Equal("/EU/Database", database.Path);
        Assert.Equal(1, database.Depth);
    }

    [Fact]
    public void RenameUpdatesEntireSubtreePath()
    {
        var root = Create("Prod");
        var child = Create("EU", root);
        var grandchild = Create("Database", child);
        Folder[] tree = [root, child, grandchild];

        var result = child.Rename("Europe", tree, GuidProvider());

        Assert.True(result.IsSuccess);
        Assert.Equal("/Prod/Europe", child.Path);
        Assert.Equal("/Prod/Europe/Database", grandchild.Path);
        Assert.Equal(1, child.Depth);
        Assert.Equal(2, grandchild.Depth);
    }

    [Fact]
    public void RenameRejectsSiblingCollisionWithoutMutation()
    {
        var root = Create("Prod");
        var eu = Create("EU", root);
        var us = Create("US", root);
        Folder[] tree = [root, eu, us];

        var result = us.Rename("eu", tree, GuidProvider());

        Assert.True(result.IsFailure);
        Assert.Equal("US", us.Name);
        Assert.Equal("/Prod/US", us.Path);
    }

    [Fact]
    public void PresentationMutationUpdatesConcurrencyMetadata()
    {
        var folder = Create("Prod");
        var oldStamp = folder.ConcurrencyStamp;
        var modified = DateTimeOffset.UtcNow.AddMinutes(1);

        _ = folder.SetPresentation(7, true, GuidProvider(), modified);

        Assert.Equal(7, folder.SortOrder);
        Assert.True(folder.IsExpanded);
        Assert.NotEqual(oldStamp, folder.ConcurrencyStamp);
        Assert.Equal(modified, folder.ModifiedUtc);
    }

    private static Folder Create(string name, Folder? parent = null)
    {
        return Folder.Create(GuidProvider(), name, parent).Value;
    }

    private static SystemGuidProvider GuidProvider()
    {
        return SystemGuidProvider.Instance;
    }
}
