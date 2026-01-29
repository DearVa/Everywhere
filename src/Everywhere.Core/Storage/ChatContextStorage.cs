using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using DynamicData;
using Everywhere.Chat;
using Everywhere.Database;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ZLinq;

namespace Everywhere.Storage;

/// <summary>
/// EF Core implementation of IChatContextStorage using explicit "snapshot diff" saves.
/// No runtime subscriptions; no EF entity graph tracking of domain objects.
/// </summary>
public sealed class ChatContextStorage(
    IDbContextFactory<ChatDbContext> dbFactory,
    IBlobStorage blobStorage,
    ILogger<ChatContextStorage> logger,
    MessagePackSerializerOptions? serializerOptions = null
) : IChatContextStorage
{
    // cache of alive instances to avoid multi-instance but same id problems
    private readonly ConcurrentDictionary<Guid, WeakReference<ChatContextMetadata>> _metadataCache = [];
    private readonly ConcurrentDictionary<Guid, WeakReference<ChatContext>> _contextCache = [];

    public async Task AddChatContextAsync(ChatContext context, CancellationToken cancellationToken = default)
    {
        if (context.Metadata.Id.Version != 7)
            throw new ArgumentException("ChatContext.Metadata.Id must be Guid v7.", nameof(context));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var exists = await db.Chats.AsNoTracking().AnyAsync(x => x.Id == context.Metadata.Id, cancellationToken);

        if (exists)
            throw new InvalidOperationException($"ChatContext {context.Metadata.Id} already exists.");

        // Insert context row
        var ctxEntity = new ChatContextEntity
        {
            Id = context.Metadata.Id,
            CreatedAt = context.Metadata.DateCreated,
            UpdatedAt = context.Metadata.DateModified,
            Topic = context.Metadata.Topic,
            IsDeleted = false
        };
        db.Chats.Add(ctxEntity);

        // Insert nodes (including root Guid.Empty if present)
        var now = DateTimeOffset.UtcNow;
        foreach (var node in context.GetAllNodes())
        {
            var entity = BuildNodeEntity(context.Metadata.Id, node, now);
            db.Nodes.Add(entity);
        }

        await db.SaveChangesAsync(cancellationToken);

        // Canonicalize cache entries (replace dead refs atomically)
        SetAlive(_contextCache, context.Metadata.Id, context);
        SetAlive(_metadataCache, context.Metadata.Id, context.Metadata);
    }

    public async Task DeleteChatContextAsync(Guid chatContextId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ctx = await db.Chats.FirstOrDefaultAsync(x => x.Id == chatContextId, cancellationToken);
        if (ctx is null) return;

        var now = DateTimeOffset.UtcNow;
        ctx.IsDeleted = true;
        ctx.UpdatedAt = now;

        await db.Nodes.Where(n => n.ChatContextId == chatContextId)
            .ExecuteUpdateAsync(
                s => s
                    .SetProperty(x => x.IsDeleted, true)
                    .SetProperty(x => x.UpdatedAt, now),
                cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        // Invalidate caches
        _contextCache.TryRemove(chatContextId, out _);
        _metadataCache.TryRemove(chatContextId, out _);
    }

    public async IAsyncEnumerable<ChatContextMetadata> QueryChatContextsAsync(
        int take,
        ChatContextOrderBy orderBy,
        bool descending,
        Guid? startAfterId = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Chats.AsNoTracking().Where(x => !x.IsDeleted);

        ChatContextEntity? anchor = null;
        if (startAfterId is { } startAfter)
        {
            anchor = await query.FirstOrDefaultAsync(x => x.Id == startAfter, cancellationToken);
        }

        query = orderBy switch
        {
            ChatContextOrderBy.UpdatedAt => ApplyOrder(query, x => x.UpdatedAt),
            ChatContextOrderBy.CreatedAt => ApplyOrder(query, x => x.CreatedAt),
            ChatContextOrderBy.Topic => ApplyOrder(query, x => x.Topic ?? string.Empty),
            _ => ApplyOrder(query, x => x.UpdatedAt),
        };

        query = query.Take(take);

        await foreach (var entity in query.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            var metadata = GetOrAddAlive(
                _metadataCache,
                entity.Id,
                () => new ChatContextMetadata(entity.Id, entity.CreatedAt, entity.UpdatedAt, entity.Topic));

            yield return metadata;
        }

        IQueryable<ChatContextEntity> ApplyOrder<TKey>(
            IQueryable<ChatContextEntity> baseQuery,
            Expression<Func<ChatContextEntity, TKey>> keySelector)
        {
            IQueryable<ChatContextEntity> ordered = descending ?
                baseQuery.OrderByDescending(keySelector).ThenByDescending(x => x.Id) :
                baseQuery.OrderBy(keySelector).ThenBy(x => x.Id);

            if (anchor is null) return ordered;

            // Cursor filter with (key, Id) as tie-breaker
            if (keySelector.Body is not MemberExpression me) return ordered;

            var name = me.Member.Name;
            switch (name)
            {
                case nameof(ChatContextEntity.UpdatedAt):
                    ordered = descending ?
                        ordered.Where(x => x.UpdatedAt < anchor.UpdatedAt || (x.UpdatedAt == anchor.UpdatedAt && x.Id.CompareTo(anchor.Id) < 0)) :
                        ordered.Where(x => x.UpdatedAt > anchor.UpdatedAt || (x.UpdatedAt == anchor.UpdatedAt && x.Id.CompareTo(anchor.Id) > 0));
                    break;
                case nameof(ChatContextEntity.CreatedAt):
                    ordered = descending ?
                        ordered.Where(x => x.CreatedAt < anchor.CreatedAt || (x.CreatedAt == anchor.CreatedAt && x.Id.CompareTo(anchor.Id) < 0)) :
                        ordered.Where(x => x.CreatedAt > anchor.CreatedAt || (x.CreatedAt == anchor.CreatedAt && x.Id.CompareTo(anchor.Id) > 0));
                    break;
                case nameof(ChatContextEntity.Topic):
                {
                    var topic = anchor.Topic ?? string.Empty;
                    ordered = descending ?
                        ordered.Where(x => string.Compare(x.Topic ?? string.Empty, topic, StringComparison.Ordinal) < 0
                            || (x.Topic ?? string.Empty) == topic && x.Id.CompareTo(anchor.Id) < 0) :
                        ordered.Where(x => string.Compare(x.Topic ?? string.Empty, topic, StringComparison.Ordinal) > 0
                            || (x.Topic ?? string.Empty) == topic && x.Id.CompareTo(anchor.Id) > 0);
                    break;
                }
            }
            return ordered;
        }
    }

    public async Task<ChatContext> GetChatContextAsync(Guid chatContextId, CancellationToken cancellationToken = default)
    {
        // 1) Fast path: alive cached context
        if (_contextCache.TryGetValue(chatContextId, out var wr) && wr.TryGetTarget(out var cached))
            return cached;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var ctxRow = await db.Chats.AsNoTracking().FirstOrDefaultAsync(x => x.Id == chatContextId, cancellationToken)
            ?? throw new KeyNotFoundException($"ChatContext {chatContextId} not found.");

        if (ctxRow.IsDeleted)
            throw new InvalidOperationException("ChatContext is soft-deleted.");

        var nodeRows = await db.Nodes.AsNoTracking()
            .Where(x => x.ChatContextId == chatContextId && !x.IsDeleted)
            .ToListAsync(cancellationToken);

        var nodeBlobRows = await db.NodeBlobs.AsNoTracking()
            .Where(x => x.ChatContextId == chatContextId)
            .ToListAsync(cancellationToken);

        var requiredBlobHashes = nodeBlobRows.Select(nb => nb.BlobSha256).Distinct().ToList();
        var blobsByHash = await db.Blobs.AsNoTracking()
            .Where(b => requiredBlobHashes.Contains(b.Sha256))
            .ToDictionaryAsync(b => b.Sha256, cancellationToken);

        // Root node
        var rootRow = nodeRows.FirstOrDefault(x => x.Id == Guid.Empty);
        if (rootRow is null || DeserializeMessage(rootRow.Payload) is not SystemChatMessage rootMessage)
            throw new InvalidOperationException("Root node (System Prompt) is missing or invalid.");

        var rootNode = new ChatMessageNode(Guid.Empty, rootMessage);

        // Build node instances
        var nodesById = new Dictionary<Guid, ChatMessageNode>();
        var childrenBuckets = new Dictionary<Guid, List<Guid>>();

        // Create non-root nodes first (we also create root below)
        foreach (var row in nodeRows)
        {
            if (row.Id == Guid.Empty) continue; // handle root later

            var msg = DeserializeMessage(row.Payload);
            if (msg is UserChatMessage userMsg)
            {
                foreach (var attachment in userMsg.Attachments.OfType<ChatFileAttachment>())
                {
                    if (blobsByHash.TryGetValue(attachment.Sha256, out var blobEntity))
                    {
                        attachment.FilePath = blobEntity.LocalPath;
                    }

                    // If blob is not found, FilePath will remain as it was from deserialization,
                    // which might be an old/invalid path. This is expected behavior if files are missing.
                }
            }

            var node = new ChatMessageNode(row.Id, msg);
            nodesById[row.Id] = node;

            if (row.ParentId is { } parentId)
            {
                if (!childrenBuckets.TryGetValue(parentId, out var list))
                {
                    childrenBuckets[parentId] = list = [];
                }
                list.Add(row.Id);
            }
        }

        // Assign children (ordered by InsertKey) and resolve ChoiceIndex via ChoiceChildId
        void ApplyChildren(Guid parentId, ChatMessageNode parentNode, ChatNodeEntity? parentRow)
        {
            if (!childrenBuckets.TryGetValue(parentId, out var list)) return;

            foreach (var childId in list.OrderBy(GetOrderKey))
            {
                parentNode.Add(childId);
                nodesById[childId].Parent = parentNode;
            }

            if (parentRow?.ChoiceChildId is { } chosen)
            {
                var idx = parentNode.Children.IndexOf(chosen);
                parentNode.ChoiceIndex = idx; // -1 if not found -> clamped in property
            }
        }

        // Apply for root
        ApplyChildren(Guid.Empty, rootNode, rootRow);

        // Apply for all parents present
        var rowById = nodeRows.ToDictionary(x => x.Id);
        foreach (var (id, node) in nodesById)
        {
            rowById.TryGetValue(id, out var row);
            ApplyChildren(id, node, row);
        }

        // Canonical metadata instance
        var metadata = GetOrAddAlive(
            _metadataCache,
            ctxRow.Id,
            () => new ChatContextMetadata(ctxRow.Id, ctxRow.CreatedAt, ctxRow.UpdatedAt, ctxRow.Topic));

        var built = new ChatContext(metadata, nodesById.Values, rootNode);

        // Canonical context instance (replace dead refs if any)
        var finalContext = GetOrAddAlive(_contextCache, chatContextId, () => built);

        return finalContext;
    }

    public async Task SaveChatContextAsync(ChatContext context, CancellationToken cancellationToken = default)
    {
        if (context.Metadata.Id.Version != 7)
            throw new ArgumentException("ChatContext.Metadata.Id must be Guid v7.", nameof(context));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var chatId = context.Metadata.Id;
        var now = DateTimeOffset.UtcNow;

        // Upsert context row
        var ctxRow = await db.Chats.FirstOrDefaultAsync(x => x.Id == chatId, cancellationToken);
        if (ctxRow is null)
        {
            ctxRow = new ChatContextEntity
            {
                Id = chatId,
                CreatedAt = context.Metadata.DateCreated,
                UpdatedAt = context.Metadata.DateModified,
                Topic = context.Metadata.Topic,
                IsDeleted = false
            };
            db.Chats.Add(ctxRow);
        }
        else
        {
            ctxRow.Topic = context.Metadata.Topic;
            ctxRow.UpdatedAt = context.Metadata.DateModified;
            // Keep CreatedAt/ServerVersion
            if (ctxRow.IsDeleted) ctxRow.IsDeleted = false;
        }

        // Load existing nodes snapshot from DB (for diff)
        var dbNodes = await db.Nodes
            .Where(x => x.ChatContextId == chatId)
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var dbNodeBlobs = await db.NodeBlobs
            .Where(x => x.ChatContextId == chatId)
            .ToListAsync(cancellationToken);
        var dbNodeBlobsSet = dbNodeBlobs
            .Select(nb => (nb.ChatNodeId, nb.BlobSha256, nb.Index))
            .ToHashSet();

        var memNodes = context.GetAllNodes().ToDictionary(x => x.Id, x => x);

        // Upsert / Update
        foreach (var (id, node) in memNodes)
        {
            if (!dbNodes.TryGetValue(id, out var row))
            {
                var entity = BuildNodeEntity(chatId, node, now);
                db.Nodes.Add(entity);
            }
            else
            {
                // Update fields
                row.ParentId = node.Parent?.Id;
                row.ChoiceChildId = ResolveChoiceChildId(node);
                row.Payload = SerializeMessage(node.Message);
                row.Author = node.Message.Role.Label.SafeSubstring(0, 10);
                row.UpdatedAt = now;
                row.IsDeleted = false;
            }

            if (node.Message is IHaveChatAttachments messageWithChatAttachments)
            {
                var fileAttachments = messageWithChatAttachments.Attachments.AsValueEnumerable().OfType<ChatFileAttachment>().ToList();
                for (var i = 0; i < fileAttachments.Count; i++)
                {
                    var attachment = fileAttachments[i];
                    var key = (node.Id, attachment.Sha256, i);

                    // If the association already exists, remove it from the set to mark it as "seen".
                    if (dbNodeBlobsSet.Remove(key))
                    {
                        continue;
                    }

                    // The association is new, so we need to ensure the blob exists and add the association.
                    var blob = await blobStorage.QueryBlobAsync(attachment.Sha256, cancellationToken);
                    if (blob is null)
                    {
                        // This indicates the blob isn't in our DB. Let's store it.
                        try
                        {
                            await blobStorage.StorageBlobAsync(attachment.FilePath, attachment.MimeType, cancellationToken: cancellationToken);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Failed to store attachment blob {Sha256} from {FilePath}", attachment.Sha256, attachment.FilePath);
                            // Ignore and continue; we don't want to fail the entire save operation due to a missing file.
                        }
                    }

                    // Add the new association record.
                    db.NodeBlobs.Add(new NodeBlobEntity
                    {
                        ChatContextId = chatId,
                        ChatNodeId = node.Id,
                        Index = i,
                        BlobSha256 = attachment.Sha256
                    });
                }
            }
        }

        // Soft-delete nodes that disappeared in memory
        foreach (var (id, row) in dbNodes)
        {
            if (!memNodes.ContainsKey(id))
            {
                row.IsDeleted = true;
                row.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(cancellationToken);

        // Canonicalize cache after save
        SetAlive(_contextCache, context.Metadata.Id, context);
        SetAlive(_metadataCache, context.Metadata.Id, context.Metadata);
    }

    public async Task SaveChatContextMetadataAsync(ChatContextMetadata metadata, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var ctxRow = await db.Chats.FirstOrDefaultAsync(x => x.Id == metadata.Id, cancellationToken);
        if (ctxRow is null) return;

        ctxRow.Topic = metadata.Topic;
        ctxRow.UpdatedAt = metadata.DateModified;

        await db.SaveChangesAsync(cancellationToken);

        SetAlive(_metadataCache, metadata.Id, metadata);
        _contextCache.TryRemove(metadata.Id, out _); // keep metadata-context coherence
    }

    // ————— helpers —————
    private static T GetOrAddAlive<T>(
        ConcurrentDictionary<Guid, WeakReference<T>> cache,
        Guid id,
        Func<T> factory) where T : class
    {
        while (true)
        {
            if (cache.TryGetValue(id, out var existingWr) && existingWr.TryGetTarget(out var existing))
                return existing;

            var created = factory();
            var newWr = new WeakReference<T>(created);

            if (existingWr is null)
            {
                if (cache.TryAdd(id, newWr))
                    return created;
                // Lost the race, retry to read the winner
            }
            else
            {
                if (cache.TryUpdate(id, newWr, existingWr))
                    return created;
                // Lost the race, retry to read the winner
            }
        }
    }

    private static void SetAlive<T>(
        ConcurrentDictionary<Guid, WeakReference<T>> cache,
        Guid id,
        T instance) where T : class
    {
        var newWr = new WeakReference<T>(instance);

        while (true)
        {
            if (!cache.TryGetValue(id, out var existingWr))
            {
                if (cache.TryAdd(id, newWr))
                    return;
                continue; // race, retry
            }

            // If the cache already points to the very same instance, nothing to do
            if (existingWr.TryGetTarget(out var existing) && ReferenceEquals(existing, instance))
                return;

            // Either dead or different instance -> replace
            if (cache.TryUpdate(id, newWr, existingWr))
                return;

            // race, retry
        }
    }

    private ChatNodeEntity BuildNodeEntity(Guid chatId, ChatMessageNode node, DateTimeOffset now) =>
        new()
        {
            ChatContextId = chatId,
            Id = node.Id,
            ParentId = node.Parent?.Id,
            ChoiceChildId = ResolveChoiceChildId(node),
            Payload = SerializeMessage(node.Message),
            Author = node.Message.Role.Label.SafeSubstring(0, 10),
            CreatedAt = now,
            UpdatedAt = now,
            IsDeleted = false
        };

    private byte[] SerializeMessage(ChatMessage message) =>
        MessagePackSerializer.Serialize(message, serializerOptions);

    private static ChatMessage DeserializeMessage(byte[] payload) =>
        MessagePackSerializer.Deserialize<ChatMessage>(payload);

    private static Guid? ResolveChoiceChildId(ChatMessageNode node)
    {
        if (node.ChoiceIndex < 0 || node.ChoiceIndex >= node.Children.Count) return null;
        return node.Children[node.ChoiceIndex];
    }

    private static long GetOrderKey(Guid id)
    {
        // Guid v7 is already ordered by timestamp, so we can use it directly
        if (id == Guid.Empty) return long.MinValue;

        Span<byte> buffer = stackalloc byte[16];
        id.TryWriteBytes(buffer, true, out _);

        // Use the first 8 bytes as a long (big-endian)
        return BinaryPrimitives.ReadInt64BigEndian(buffer);
    }
}