namespace PlanApi;

public interface IPlanRepository
{
    Task<bool> ExistsAsync(string objectId);
    Task SaveBlobAsync(string objectId, string json);
}