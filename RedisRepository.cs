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
}