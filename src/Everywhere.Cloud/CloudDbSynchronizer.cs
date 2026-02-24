#if DEBUG
using System.Net.Http.Headers;
#endif

using System.Net.Sockets;
using Everywhere.Common;
using Everywhere.Database;
using Everywhere.Extensions;
using MessagePack;
using MessagePack.Formatters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;

namespace Everywhere.Cloud;

public class CloudChatDbSynchronizer(
    IDbContextFactory<ChatDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudChatDbSynchronizer> logger
) : IChatDbSynchronizer, IAsyncInitializer
{
    public AsyncInitializerIndex Index => AsyncInitializerIndex.Database + 1;

    private readonly AsyncLock _syncLock = new();
    private const int PushBytesLimit = 5 * 1024 * 1024; // 5 MB

    public Task InitializeAsync()
    {
        // If the cloud sync base URL is not configured, skip synchronization.
        if (CloudConstants.CloudSyncBaseUrl.IsNullOrEmpty()) return Task.CompletedTask;

        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    await SynchronizeAsync();
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpRequestException ex) when (ex.InnerException is SocketException)
                {
                    // Network-related errors
                    logger.LogInformation(ex, "Network error occurred during cloud database synchronization.");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error occurred during cloud database synchronization.");
                }

                await Task.Delay(TimeSpan.FromMinutes(1d));
            }
        }).Detach(logger.ToExceptionHandler());

        return Task.CompletedTask;
    }

    public async Task SynchronizeAsync(CancellationToken cancellationToken = default)
    {
        using var _ = await _syncLock.LockAsync(cancellationToken);

        await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);
        var metadata = await dbContext.SyncMetadata.FirstOrDefaultAsync(
            x => x.Id == CloudSyncMetadataEntity.SingletonId,
            cancellationToken: cancellationToken);
        if (metadata is null) return;

        // Use named HttpClient for ICloudClient to ensure proper configuration (e.g., authentication, proxy).
#if DEBUG
        using var httpClient = httpClientFactory.CreateClient();
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            "dev-user-pro");
        httpClient.DefaultRequestHeaders.Add("X-Device-Name", Environment.MachineName);
#else
        using var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));
#endif
        httpClient.Timeout = TimeSpan.FromMinutes(5); // Set a reasonable timeout for sync operations

        try
        {
            // 1. Pull remote changes from the cloud
            // set syncing flag to avoid interference with other operations
            dbContext.IsSyncing = true;
            await PullChangesAsync(dbContext, metadata, httpClient, cancellationToken);
        }
        finally
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            dbContext.IsSyncing = false;
        }

        try
        {
            // 2. Push local changes to the cloud
            await PushChangesAsync(dbContext, metadata, httpClient, cancellationToken);
        }
        finally
        {
            // save metadata changes
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async ValueTask PullChangesAsync(
        ChatDbContext dbContext,
        CloudSyncMetadataEntity metadata,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            logger.LogDebug("Pulling cloud changes since version {LastPulledVersion}", metadata.LastPulledVersion);

            var response = await httpClient.GetAsync(
                new Uri($"{CloudConstants.CloudSyncBaseUrl}/chat-db/pull?sinceVersion={Math.Max(metadata.LastPulledVersion, 0L)}"),
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                // This will throw an exception with detailed error info.
                await ApiPayload.EnsureSuccessFromHttpResponseJsonAsync(response, cancellationToken: cancellationToken);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            if (stream is null)
            {
                throw new HttpRequestException("Failed to deserialize cloud pull response payload.");
            }

            var data = await MessagePackSerializer.DeserializeAsync<CloudPullData>(
                stream,
                cancellationToken: cancellationToken);

            // Apply pulled version regardless of whether there are items to process.
            metadata.LastPulledVersion = data.LatestVersion;
            metadata.LastSyncAt = DateTimeOffset.UtcNow;

            if (data.EntityWrappers is not { Count: > 0 }) return;

            var chats = dbContext.Chats;
            var nodes = dbContext.Nodes;
            foreach (var entityWrapper in data.EntityWrappers)
            {
                if (entityWrapper.Data is null)
                {
                    var existedNode = await FindEntityAsync(entityWrapper.Id);
                    if (existedNode is not null)
                    {
                        existedNode.IsDeleted = true;
                        existedNode.LocalSyncVersion = ICloudSyncable.UnmodifiedFromCloud;
                    }
                    else
                    {
                        logger.LogWarning("Received delete for non-existing entity Id {EntityId} from cloud.", entityWrapper.Id);
                    }
                }
                else
                {
                    CloudSyncableEntity entity;
                    try
                    {
                        entity = MessagePackSerializer.Deserialize<CloudSyncableEntity>(
                            entityWrapper.Data,
                            cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to deserialize a pulled cloud entity.");
                        continue;
                    }

                    if (entityWrapper.Id == Guid.Empty)
                    {
                        // Invalid Id, skip it.
                        continue;
                    }

                    // entity.Id is not serialized/deserialized, so we need to set it manually.
                    entity.Id = entityWrapper.Id;
                    entity.LocalSyncVersion = ICloudSyncable.UnmodifiedFromCloud;

                    var existingEntity = await FindEntityAsync(entity.Id);
                    if (existingEntity is null)
                    {
                        switch (entity)
                        {
                            case ChatContextEntity chat:
                            {
                                await chats.AddAsync(chat, cancellationToken);
                                break;
                            }
                            case ChatNodeEntity node:
                            {
                                await nodes.AddAsync(node, cancellationToken);
                                break;
                            }
                        }
                    }
                    else if (existingEntity.LocalSyncVersion <= metadata.LastPushedVersion)
                    {
                        // Only update(replace) if the local entity is unmodified since last pull
                        // In the other words, if existedEntity.SyncVersion > metadata.LastPushedVersion,
                        // it means the local entity has been modified locally after last pull,
                        // so we should not overwrite it with the pulled entity.
                        // It needs to be pushed again in the next sync cycle.
                        dbContext.Entry(existingEntity).CurrentValues.SetValues(entity);
                    }
                }
            }

            if (!data.HasMore) break;
        }

        // Find nodes first because it has a higher chance of being requested.
        async Task<CloudSyncableEntity?> FindEntityAsync(Guid id) =>
            await dbContext.Nodes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken) as CloudSyncableEntity ??
            await dbContext.Chats.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken);
    }

    private async ValueTask PushChangesAsync(
        ChatDbContext dbContext,
        CloudSyncMetadataEntity metadata,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var lastPushedVersion = metadata.LastPushedVersion;
        var currentPushedVersion = lastPushedVersion;

        // Gather all entities that have LocalSyncVersion > lastPushedVersion (modified since last push)
        // Since EF Core does not support polymorphic queries, we need to query each DbSet separately.
        var entitiesToPush = new List<CloudSyncableEntity>();
        entitiesToPush.AddRange(
            await dbContext.Chats
                .AsNoTracking()
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
                .Where(x => x.Id != Guid.Empty)
                .ToListAsync(cancellationToken: cancellationToken));
        entitiesToPush.AddRange(
            await dbContext.Nodes
                .AsNoTracking()
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
                .Where(x => x.Id != Guid.Empty)
                .ToListAsync(cancellationToken: cancellationToken));
        if (entitiesToPush.Count == 0) return;

        var entityWrappers = new List<EntityWrapper>(entitiesToPush.Count);
        var totalPushBytes = 0;

        // Sort entities by LocalSyncVersion in ascending order to push older changes first.
        // Even though a chunk is failed to push, metadata.LastPushedVersion will be updated to the highest
        // successfully pushed version, so the next sync cycle will retry the failed entities.
        entitiesToPush.Sort((x, y) => x.LocalSyncVersion.CompareTo(y.LocalSyncVersion));

        foreach (var entity in entitiesToPush)
        {
            EntityWrapper entityWrapper;
            if (entity.IsDeleted)
            {
                // If the entity is marked as deleted, we only need to send its Id and null data.
                entityWrapper = new EntityWrapper(entity.Id, null);
            }
            else
            {
                var data = MessagePackSerializer.Serialize(entity, cancellationToken: cancellationToken);
                if (data.Length > PushBytesLimit)
                {
                    logger.LogWarning(
                        "Entity [{EntityType}] {Entity} exceeds the push size limit and will be skipped.",
                        entity.GetType().Name,
                        entity.ToString());

                    entity.LocalSyncVersion = ICloudSyncable.NotVersioned;
                    continue;
                }

                entityWrapper = new EntityWrapper(entity.Id, data);
            }

            entityWrappers.Add(entityWrapper);
            totalPushBytes += (entityWrapper.Data?.Length ?? 0) + 64; // Approximate overhead for Id and IsDeleted
            currentPushedVersion = Math.Max(entity.LocalSyncVersion, currentPushedVersion); // Update the current pushed version

            if (totalPushBytes > PushBytesLimit)
            {
                // Chunk reached the size limit, push what we have so far.
                await PushPayloadAsync(entityWrappers, httpClient, cancellationToken);
                entityWrappers.Clear();
                totalPushBytes = 0;
                metadata.LastPushedVersion = currentPushedVersion; // If successful, update the last pushed version
                metadata.LastSyncAt = DateTimeOffset.UtcNow;
            }
        }

        await PushPayloadAsync(entityWrappers, httpClient, cancellationToken);
        metadata.LastPushedVersion = currentPushedVersion; // If successful, update the last pushed version
        metadata.LastSyncAt = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Pushes local changes to the remote cloud storage.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="httpClient"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>The highest SyncVersion that was successfully pushed.</returns>
    private async ValueTask PushPayloadAsync(List<EntityWrapper> payload, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var data = MessagePackSerializer.Serialize(payload, cancellationToken: cancellationToken);
        var response = await httpClient.PostAsync(
            new Uri($"{CloudConstants.CloudSyncBaseUrl}/chat-db/push"),
            new ByteArrayContent(data),
            cancellationToken);
        var result = await ApiPayload.EnsureSuccessFromHttpResponseJsonAsync(response, cancellationToken: cancellationToken);
        logger.LogDebug("Pushed {EntityCount} entities to cloud successfully: {Result}", payload.Count, result);
    }
}

/// <summary>
/// Wrapper for an entity to be synchronized.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record EntityWrapper(
    [property: Key(0), MessagePackFormatter(typeof(NativeGuidFormatter))] Guid Id,
    [property: Key(1)] byte[]? Data
);

/// <summary>
/// Api payload for cloud pull response.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class CloudPullData
{
    [Key(0)]
    public long LatestVersion { get; set; }

    [Key(1)]
    public bool HasMore { get; set; }

    [Key(2)]
    public List<EntityWrapper>? EntityWrappers { get; set; }
}