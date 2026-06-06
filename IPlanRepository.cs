namespace PlanApi;
using System.Text.Json.Nodes;

public interface IPlanRepository
{
    Task<bool> ExistsAsync(string objectId);
    Task SaveFlattenedAsync(IReadOnlyDictionary<string, JsonObject> records);
}