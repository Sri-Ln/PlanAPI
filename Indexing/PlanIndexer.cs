using System.Text.Json.Nodes;
using Elastic.Clients.Elasticsearch;

namespace PlanApi.Indexing;

public interface IPlanIndexer
{
    Task IndexPlanAsync(JsonObject plan, CancellationToken ct = default);
    Task DeletePlanAsync(IReadOnlyList<string> docIds, string routing, CancellationToken ct = default);
}

// Thin wrapper over ElasticsearchClient. Throws on failure so the consumer can nack and retry.
public sealed class PlanIndexer : IPlanIndexer
{
    private readonly ElasticsearchClient _client;
    private readonly string _index;
    private readonly ILogger<PlanIndexer> _log;

    public PlanIndexer(IConfiguration config, ILogger<PlanIndexer> log)
    {
        _log = log;
        _index = config["Elasticsearch:Index"]
                 ?? throw new InvalidOperationException("Elasticsearch:Index is not configured");
        var uri = config["Elasticsearch:Uri"]
                  ?? throw new InvalidOperationException("Elasticsearch:Uri is not configured");

        // The client is thread-safe and pools connections — build it once, share it.
        _client = new ElasticsearchClient(new ElasticsearchClientSettings(new Uri(uri)).DefaultIndex(_index));
    }

    public async Task IndexPlanAsync(JsonObject plan, CancellationToken ct = default)
    {
        var docs = EsFlattener.Flatten(plan);

        // One bulk call for the whole tree. Refresh.WaitFor holds the response until the
        // documents are searchable, so a Kibana query run straight after the HTTP call
        // already sees them instead of losing a race with the ~1s refresh interval.
        var response = await _client.BulkAsync(_index, bulk =>
        {
            bulk.Refresh(Refresh.WaitFor);
            foreach (var doc in docs)
                bulk.Index(doc.Source, op => op.Id(doc.Id).Routing(doc.Routing));
        }, ct);

        if (!response.IsValidResponse || response.Errors)
            throw new InvalidOperationException($"bulk index failed: {response.DebugInformation}");

        _log.LogInformation("Indexed {Count} documents for plan {PlanId}", docs.Count, docs[0].Id);
    }

    public async Task DeletePlanAsync(IReadOnlyList<string> docIds, string routing, CancellationToken ct = default)
    {
        if (docIds.Count == 0) return;

        var response = await _client.BulkAsync(_index, bulk =>
        {
            bulk.Refresh(Refresh.WaitFor);

            // Every delete carries the ROOT plan id as routing, including the grandchildren.
            // Routing decides which shard is asked; with the wrong value Elasticsearch asks a
            // shard that never held the document and reports "not found" rather than an error.
            foreach (var id in docIds)
                bulk.Delete(id, op => op.Routing(routing));
        }, ct);

        if (!response.IsValidResponse || response.Errors)
            throw new InvalidOperationException($"bulk delete failed: {response.DebugInformation}");

        _log.LogInformation("Deleted {Count} documents for plan {PlanId}", docIds.Count, routing);
    }
}
