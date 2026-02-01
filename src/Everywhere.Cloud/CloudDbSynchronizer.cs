using Everywhere.Common;
using Everywhere.Database;
using Everywhere.Extensions;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Nito.AsyncEx;

namespace Everywhere.Cloud;

public class CloudChatDbSynchronizer(
    IDbContextFactory<ChatDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<CloudChatDbSynchronizer> logger
) : IChatDbSynchronizer
{
    public AsyncInitializerPriority Priority => AsyncInitializerPriority.Database + 1;

    private readonly AsyncLock _syncLock = new();
    private const int PushBytesLimit = 5 * 1024 * 1024; // 5 MB

    public Task InitializeAsync()
    {
        Task.Run(async () =>
        {
            // TODO: adjust delayMinutes dynamically after manual syncs
            var delayMinutes = 1d;
            while (true)
            {
                try
                {
                    await SynchronizeAsync();
                    delayMinutes = 1d; // reset delay on success
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    delayMinutes = Math.Min(2 * delayMinutes, 30); // exponential backoff

                    logger.LogError(ex, "Error occurred during cloud database synchronization.");
                }

                await Task.Delay(TimeSpan.FromMinutes(delayMinutes));
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
        using var httpClient = httpClientFactory.CreateClient(nameof(ICloudClient));

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
        var lastPulledVersion = metadata.LastPulledVersion;

        // TODO: x-client-id
        var response = await httpClient.GetAsync(
            new Uri($"https://api.sylinko.com/everywhere/sync/pull?sinceVersion={lastPulledVersion}"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (stream is null)
        {
            throw new HttpRequestException("Failed to deserialize cloud pull response payload.");
        }

        var payload = await MessagePackSerializer.DeserializeAsync<CloudPullApiPayload>(
            stream,
            cancellationToken: cancellationToken);
        var data = payload.EnsureData();

        // Apply pulled version regardless of whether there are items to process.
        metadata.LastPulledVersion = data.LatestVersion;

        if (data.EntityWrappers is not { Count: > 0 }) return;

        var dbSet = dbContext.Set<ICloudSyncable>();
        foreach (var entityWrapper in data.EntityWrappers)
        {
            if (entityWrapper.IsDeleted)
            {
                var existedEntity = await dbSet.FirstOrDefaultAsync(x => x.Id == entityWrapper.Id, cancellationToken: cancellationToken);
                if (existedEntity is not null)
                {
                    dbSet.Remove(existedEntity);
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

                // entity.Id is not serialized/deserialized, so we need to set it manually.
                entity.Id = entityWrapper.Id;
                entity.LocalSyncVersion = ICloudSyncable.UnmodifiedFromCloud;
                var existingEntity = await dbSet.FirstOrDefaultAsync(x => x.Id == entity.Id, cancellationToken: cancellationToken);
                if (existingEntity is null)
                {
                    dbSet.Add(entity);
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
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
                .ToListAsync(cancellationToken: cancellationToken));
        entitiesToPush.AddRange(
            await dbContext.Nodes
                .Where(x => x.LocalSyncVersion > lastPushedVersion)
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
                // If the entity is marked as deleted, we only need to send its Id and IsDeleted flag.
                entityWrapper = new EntityWrapper(entity.Id, true, null);
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

                entityWrapper = new EntityWrapper(entity.Id, false, data);
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
            }
        }

        await PushPayloadAsync(entityWrappers, httpClient, cancellationToken);
        metadata.LastPushedVersion = currentPushedVersion; // If successful, update the last pushed version
    }

    /// <summary>
    /// Pushes local changes to the remote cloud storage.
    /// </summary>
    /// <param name="payload"></param>
    /// <param name="httpClient"></param>
    /// <param name="cancellationToken"></param>
    /// <returns>The highest SyncVersion that was successfully pushed.</returns>
    private async static ValueTask PushPayloadAsync(List<EntityWrapper> payload, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var data = MessagePackSerializer.Serialize(payload, cancellationToken: cancellationToken);
        // TODO: x-client-id
        await httpClient.PostAsync(new Uri("https://api.sylinko.com/everywhere/sync/push"), new ByteArrayContent(data), cancellationToken);
    }
}

/// <summary>
/// Wrapper for an entity to be synchronized.
/// </summary>
[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial record EntityWrapper(
    [property: Key(0)] Guid Id,
    [property: Key(1)] bool IsDeleted,
    [property: Key(2)] byte[]? Data
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
    public List<EntityWrapper>? EntityWrappers { get; set; }
}

[MessagePackObject(OnlyIncludeKeyedMembers = true, AllowPrivate = true)]
public sealed partial class CloudPullApiPayload : ApiPayload<CloudPullData>;