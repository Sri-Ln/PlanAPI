namespace PlanApi;

public interface IPlanRepository
{
    Task<bool> ExistsAsync(string objectId);
}