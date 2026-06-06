using StackExchange.Redis;
using System.Text.Json.Nodes;

namespace PlanApi;

public class RedisRepository : IPlanRepository
{
    private readonly IDatabase _db;

    public RedisRepository(IConnectionMultiplexer mux)
    {
        _db = mux.GetDatabase();
    }

    public Task<bool> ExistsAsync(string objectId) =>
        _db.KeyExistsAsync($"plan:{objectId}");
    
    public async Task SaveFlattenedAsync(IReadOnlyDictionary<string, JsonObject> records)
    {
        var writes = records.Select(kvp => _db.StringSetAsync(kvp.Key, kvp.Value.ToJsonString()));
        await Task.WhenAll(writes);
    }
    
    public Task<JsonObject?> GetAsync(string objectId) =>
        PlanFlattener.AssembleAsync($"plan:{objectId}", ReadRawAsync);

    private async Task<string?> ReadRawAsync(string key)
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }
    
    public async Task<bool> DeleteAsync(string objectId)
    {
        var rootKey = $"plan:{objectId}";
        var keys = await PlanFlattener.CollectKeysAsync(rootKey, ReadRawAsync);
        if (keys.Count == 0) return false;

        var redisKeys = keys.Select(k => (RedisKey)k).ToArray();
        await _db.KeyDeleteAsync(redisKeys);
        return true;
    }
}