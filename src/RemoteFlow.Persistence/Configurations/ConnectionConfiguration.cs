#pragma warning disable IDE0058 // EF's fluent configuration API returns builders that are intentionally ignored.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RemoteFlow.Domain.Entities;

namespace RemoteFlow.Persistence.Configurations;

internal sealed class ConnectionConfiguration : IEntityTypeConfiguration<Connection>
{
    public void Configure(EntityTypeBuilder<Connection> builder)
    {
        builder.ToTable("Connections");
        builder.HasKey(connection => connection.Id);

        builder.Property(connection => connection.Id).HasTextGuidConversion();
        builder.Property(connection => connection.Name).HasMaxLength(100).UseCollation("NOCASE").IsRequired();
        builder.Property(connection => connection.Host).HasMaxLength(255).UseCollation("NOCASE").IsRequired();
        builder.Property(connection => connection.Protocol).HasConversion<int>();
        builder.Property(connection => connection.Username).HasMaxLength(255);
        builder.Property(connection => connection.AuthMethod).HasConversion<int>();
        builder.Property(connection => connection.Notes).HasMaxLength(4_000);
        builder.Property(connection => connection.FolderId).HasTextGuidConversion();
        builder.Property(connection => connection.Environment).HasConversion<int>();
        builder.Property(connection => connection.ColorOverrideHex).HasMaxLength(7);
        builder.Property(connection => connection.ConcurrencyStamp)
            .HasTextGuidConversion()
            .IsConcurrencyToken();

        builder.HasOne<Folder>()
            .WithMany()
            .HasForeignKey(connection => connection.FolderId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(connection => connection.FolderId);
        builder.HasIndex(connection => connection.Name);
        builder.HasIndex(connection => connection.Host);
        builder.HasIndex(connection => connection.Protocol);
        builder.HasIndex(connection => connection.IsFavorite)
            .HasFilter("\"IsFavorite\" = 1");

        ConfigureCredential(builder);
        ConfigureSsh(builder);
        ConfigureSftp(builder);
        ConfigureRdp(builder);
        ConfigureObjectStorage(builder);
    }

    private static void ConfigureCredential(EntityTypeBuilder<Connection> builder)
    {
        builder.OwnsOne(connection => connection.Credential, owned =>
        {
            owned.Property(credential => credential.Kind)
                .HasColumnName("Credential_Kind")
                .HasConversion<int>();
            owned.Property(credential => credential.StoreKey)
                .HasColumnName("Credential_StoreKey")
                .HasMaxLength(512)
                .IsRequired();
            owned.Property(credential => credential.StoreProvider)
                .HasColumnName("Credential_StoreProvider")
                .HasMaxLength(100)
                .IsRequired();
            owned.Property(credential => credential.UpdatedUtc)
                .HasColumnName("Credential_UpdatedUtc");
        });
        builder.Navigation(connection => connection.Credential).IsRequired();
    }

    private static void ConfigureSsh(EntityTypeBuilder<Connection> builder)
    {
        builder.OwnsOne(connection => connection.Ssh, owned =>
        {
            owned.Property(options => options.KeepAliveSeconds).HasColumnName("Ssh_KeepAliveSeconds");
            owned.Property(options => options.TerminalType)
                .HasColumnName("Ssh_TerminalType")
                .HasMaxLength(100)
                .IsRequired();
            owned.Property(options => options.PrivateKeyPath)
                .HasColumnName("Ssh_PrivateKeyPath")
                .HasMaxLength(4_096);
            owned.Property(options => options.InitialCommand)
                .HasColumnName("Ssh_InitialCommand")
                .HasMaxLength(4_000);
            owned.Property(options => options.StartupDirectory)
                .HasColumnName("Ssh_StartupDirectory")
                .HasMaxLength(4_096);
            owned.Property(options => options.HostKeyPolicy)
                .HasColumnName("Ssh_HostKeyPolicy")
                .HasConversion<int>();
            owned.Property(options => options.RequestPty).HasColumnName("Ssh_RequestPty");
        });
        builder.Navigation(connection => connection.Ssh).IsRequired();
    }

    private static void ConfigureSftp(EntityTypeBuilder<Connection> builder)
    {
        builder.OwnsOne(connection => connection.Sftp, owned =>
        {
            owned.Property(options => options.RemoteRootPath)
                .HasColumnName("Sftp_RemoteRootPath")
                .HasMaxLength(4_096);
            owned.Property(options => options.LocalDownloadPath)
                .HasColumnName("Sftp_LocalDownloadPath")
                .HasMaxLength(4_096);
            owned.Property(options => options.PreserveTimestamps).HasColumnName("Sftp_PreserveTimestamps");
            owned.Property(options => options.ShowHiddenFiles).HasColumnName("Sftp_ShowHiddenFiles");
        });
        builder.Navigation(connection => connection.Sftp).IsRequired();
    }

    private static void ConfigureRdp(EntityTypeBuilder<Connection> builder)
    {
        builder.OwnsOne(connection => connection.Rdp, owned =>
        {
            owned.Property(options => options.Domain).HasColumnName("Rdp_Domain").HasMaxLength(255);
            owned.Property(options => options.FullScreen).HasColumnName("Rdp_FullScreen");
            owned.Property(options => options.Width).HasColumnName("Rdp_Width");
            owned.Property(options => options.Height).HasColumnName("Rdp_Height");
            owned.Property(options => options.Multimon).HasColumnName("Rdp_Multimon");
            owned.Property(options => options.RedirectClipboard).HasColumnName("Rdp_RedirectClipboard");
            owned.Property(options => options.RedirectDrives).HasColumnName("Rdp_RedirectDrives");
        });
        builder.Navigation(connection => connection.Rdp).IsRequired();
    }

    /// <summary>One owned block for both object-storage providers. Every string column is nullable and
    /// <c>UsePathStyleAddressing</c> is not: an owned type whose every column is NULL materialises as null,
    /// and the required navigation below would then throw on query. The non-nullable bool keeps one column
    /// populated and makes the migration purely additive with a single default.</summary>
    private static void ConfigureObjectStorage(EntityTypeBuilder<Connection> builder)
    {
        builder.OwnsOne(connection => connection.ObjectStorage, owned =>
        {
            owned.Property(options => options.Region).HasColumnName("Storage_Region").HasMaxLength(100);
            owned.Property(options => options.ServiceUrl).HasColumnName("Storage_ServiceUrl").HasMaxLength(2_048);
            owned.Property(options => options.UsePathStyleAddressing)
                .HasColumnName("Storage_UsePathStyleAddressing");
            owned.Property(options => options.Container).HasColumnName("Storage_Container").HasMaxLength(63);
            owned.Property(options => options.RootPrefix).HasColumnName("Storage_RootPrefix").HasMaxLength(1_024);
            owned.Property(options => options.LocalDownloadPath)
                .HasColumnName("Storage_LocalDownloadPath")
                .HasMaxLength(4_096);
        });
        builder.Navigation(connection => connection.ObjectStorage).IsRequired();
    }
}
