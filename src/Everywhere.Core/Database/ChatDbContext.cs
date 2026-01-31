using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using Everywhere.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Everywhere.Database;

/// <summary>
/// EF Core DbContext for local chat storage.
/// </summary>
[method: DynamicDependency(DynamicallyAccessedMemberTypes.AllConstructors, typeof(DateTimeToTicksConverter))]
public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public DbSet<ChatContextEntity> Chats => Set<ChatContextEntity>();
    public DbSet<ChatNodeEntity> Nodes => Set<ChatNodeEntity>();
    public DbSet<NodeBlobEntity> NodeBlobs => Set<NodeBlobEntity>();
    public DbSet<BlobEntity> Blobs => Set<BlobEntity>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        // Chat context
        builder.Entity<ChatContextEntity>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UpdatedAt);
            // Optional: global query filter to exclude soft-deleted by default, if desired:
            // e.HasQueryFilter(x => !x.IsDeleted);
        });

        // Chat node (message tree node)
        builder.Entity<ChatNodeEntity>(e =>
        {
            // Composite PK makes (ChatContextId, ID) the unique key for a node.
            e.HasKey(x => new { ChatId = x.ChatContextId, NodeId = x.Id });

            // Stable sibling ordering by InsertKey under the same parent.
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.ParentId, x.Id }).IsUnique();

            // Filter by deletion flag.
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.IsDeleted });

            // Optionally speed up "follow current branch" by chosen child id:
            e.HasIndex(x => new { ChatId = x.ChatContextId, x.ChoiceChildId });

            e.Property(x => x.Payload).IsRequired();
        });

        // Node-to-blob association (multiple attachments per node, ordered)
        builder.Entity<NodeBlobEntity>(e =>
        {
            // PK includes Index to allow multiple blobs per node and keep order.
            e.HasKey(x => new { ChatId = x.ChatContextId, NodeId = x.ChatNodeId, x.Index });

            // Fast lookup by blob id for GC/existence checks.
            e.HasIndex(x => x.BlobSha256);

            // FK to node; cascade on delete.
            e.HasOne<ChatNodeEntity>()
                .WithMany()
                .HasForeignKey(x => new { ChatId = x.ChatContextId, NodeId = x.ChatNodeId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Local blob metadata (content-addressed)
        builder.Entity<BlobEntity>(e =>
        {
            e.HasKey(x => x.Sha256);
            e.HasIndex(x => x.LastAccessAt);
        });
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        builder
            .Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToTicksConverter>();
    }

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.AllConstructors)]
    private class DateTimeOffsetToTicksConverter() : ValueConverter<DateTimeOffset, long>(v => v.Ticks, v => new DateTimeOffset(v, TimeSpan.Zero));
}

public class ChatDbInitializer(IDbContextFactory<ChatDbContext> dbFactory) : IAsyncInitializer
{
    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Database;

    public async Task InitializeAsync()
    {
        await using var dbContext = await dbFactory.CreateDbContextAsync();
        await dbContext.Database.MigrateAsync();
    }
}

/// <summary>
/// Top-level chat context (a conversation).
/// This row holds context-wide metadata used for listing, sorting, and synchronization.
/// Messages/turns are stored as tree nodes in <see cref="ChatNodeEntity"/>.
/// </summary>
public sealed class ChatContextEntity
{
    /// <summary>
    /// Globally unique ID (Guid v7 recommended for better locality).
    /// </summary>
    public required Guid Id { get; init; }

    /// <summary>
    /// Creation time (local clock). Used for sorting; not authoritative for sync conflict resolution.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Last modified time (local clock). Bump on any change within this context to support LWW at context level.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Optional short title/topic for this chat. Limited to 64 chars.
    /// </summary>
    [MaxLength(64)]
    public string? Topic { get; set; }

    /// <summary>
    /// Soft-delete flag. When true, the context should be hidden from normal listings but retained for sync/restore.
    /// </summary>
    public bool IsDeleted { get; set; }
}

/// <summary>
/// A node in the chat message tree (root + alternating user/assistant/tool messages with branches).
/// Stores the serialized message payload (MessagePack).
/// </summary>
public sealed class ChatNodeEntity
{
    /// <summary>
    /// Parent chat context Id.
    /// </summary>
    public Guid ChatContextId { get; init; }

    /// <summary>
    /// Node Id (Guid v7). Unique within its chat context; stable across devices.
    /// </summary>
    public Guid Id { get; init; } = Guid.CreateVersion7();

    /// <summary>
    /// Parent node ID. Null for the root node.
    /// </summary>
    public Guid? ParentId { get; set; }

    /// <summary>
    /// The "chosen" child of this node (persisted as ID to avoid index shifting on concurrent insert).
    /// Map to in-memory ChoiceIndex by resolving children ordered by <see cref="Id"/>.
    /// Null means no child is chosen.
    /// </summary>
    public Guid? ChoiceChildId { get; set; }

    /// <summary>
    /// Serialized message payload (MessagePack binary of ChatMessage).
    /// </summary>
    public required byte[] Payload { get; set; }

    /// <summary>
    /// Role/author of the message (e.g., "system", "assistant", "user", "action", "tool").
    /// </summary>
    [MaxLength(10)]
    public string? Author { get; set; }

    /// <summary>
    /// Local creation time of this node (when first inserted).
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Local last modified time (bump on payload update, branching changes that affect this node, etc.).
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Soft-delete flag.
    /// </summary>
    public bool IsDeleted { get; set; }
}

/// <summary>
/// Associates a chat node with an attachment blob (content-addressed by SHA-256).
/// Multiple attachments per node are supported and preserved in the original order via <see cref="Index"/>.
/// </summary>
public sealed class NodeBlobEntity
{
    /// <summary>
    /// Parent chat context ID.
    /// </summary>
    public required Guid ChatContextId { get; init; }

    /// <summary>
    /// Chat node ID to which the blob is attached.
    /// </summary>
    public required Guid ChatNodeId { get; init; }

    /// <summary>
    /// Zero-based order of this attachment in the node.
    /// </summary>
    public required int Index { get; init; }

    /// <summary>
    /// Hex-encoded SHA-256 (lowercase). This is the content address and storage key.
    /// </summary>
    [MaxLength(64)]
    public required string BlobSha256 { get; init; }
}

/// <summary>
/// Local metadata for content-addressed blobs (files/images).
/// The actual file path convention can be: {BlobBasePath}/{CreatedAt:yyyyMMdd}/{Sha256}.
/// </summary>
public sealed class BlobEntity
{
    /// <summary>
    /// Hex-encoded SHA-256 (lowercase). Primary key.
    /// </summary>
    [MaxLength(64)]
    public required string Sha256 { get; init; }

    /// <summary>
    /// Local file path where the blob is stored.
    /// </summary>
    [MaxLength(1024)]
    public required string LocalPath { get; init; }

    /// <summary>
    /// MIME type, e.g., "image/png".
    /// </summary>
    [MaxLength(255)]
    public required string MimeType { get; init; }

    /// <summary>
    /// Size in bytes.
    /// </summary>
    public required long Size { get; init; }

    /// <summary>
    /// Local creation time.
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Last access time (for LRU/GC decisions).
    /// </summary>
    public DateTimeOffset LastAccessAt { get; set; }
}