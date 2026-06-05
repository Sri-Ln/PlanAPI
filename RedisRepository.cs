using StackExchange.Redis;

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
}