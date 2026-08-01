using System.Text.Json.Nodes;

namespace PlanApi.Messaging;

// A `record` is an immutable data holder with value equality — like a Java record.
// This is the queue contract: what the API tells the indexer to do.
//   create/update -> Plan carries the full nested plan; the consumer re-indexes the whole tree.
//   delete        -> DocIds carries every objectId in the tree, read before Redis deleted it.
public record PlanMessage(
    string Op,
    string PlanId,
    JsonObject? Plan = null,
    IReadOnlyList<string>? DocIds = null)
{
    public static PlanMessage Create(string planId, JsonObject plan) => new("create", planId, plan);
    public static PlanMessage Update(string planId, JsonObject plan) => new("update", planId, plan);
    public static PlanMessage Delete(string planId, IReadOnlyList<string> docIds) =>
        new("delete", planId, DocIds: docIds);
}
