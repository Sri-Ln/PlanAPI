using System.Text.Json.Nodes;

namespace PlanApi.Indexing;

// One Elasticsearch document produced from one object in the plan tree.
// Source already contains the plan_join field, so the indexer can write it as-is.
public record EsDocument(string Id, string Routing, JsonObject Source);

// Decomposes a nested plan into the flat parent-child documents the `demo` index expects.
// Pure: no I/O, no client, no config — so its output can be inspected without a running
// Elasticsearch. That matters because a mistake here yields empty query results, not errors.
public static class EsFlattener
{
    // ES join relation names, taken from the index mapping. These are deliberately NOT the
    // JSON field names and NOT the objectType values. Note the capital S below: the mapping
    // says planServiceCostShares while schema.json says planserviceCostShares.
    private const string RelPlan                  = "plan";
    private const string RelPlanCostShares        = "planCostShares";
    private const string RelLinkedPlanServices    = "linkedPlanServices";
    private const string RelLinkedService         = "linkedService";
    private const string RelPlanServiceCostShares = "planServiceCostShares";

    // JSON field names, taken from schema.json. Lowercase s on the last one.
    private const string FieldPlanCostShares        = "planCostShares";
    private const string FieldLinkedPlanServices    = "linkedPlanServices";
    private const string FieldLinkedService         = "linkedService";
    private const string FieldPlanServiceCostShares = "planserviceCostShares";

    public static IReadOnlyList<EsDocument> Flatten(JsonObject plan)
    {
        var planId = IdOf(plan);
        var docs = new List<EsDocument> { Doc(plan, planId, planId, RelPlan, parentId: null) };

        if (plan[FieldPlanCostShares] is JsonObject costShares)
            docs.Add(Doc(costShares, IdOf(costShares), planId, RelPlanCostShares, parentId: planId));

        if (plan[FieldLinkedPlanServices] is JsonArray services)
        {
            foreach (var item in services)
            {
                if (item is not JsonObject planService) continue;

                var planServiceId = IdOf(planService);
                docs.Add(Doc(planService, planServiceId, planId, RelLinkedPlanServices, parentId: planId));

                // Depth 2. Routing stays the ROOT plan id so the whole tree shares a shard,
                // but the parent is this linkedPlanServices object, not the plan. Two different
                // values answering two different questions: which shard, and whose child.
                if (planService[FieldLinkedService] is JsonObject linkedService)
                    docs.Add(Doc(linkedService, IdOf(linkedService), planId, RelLinkedService,
                                 parentId: planServiceId));

                if (planService[FieldPlanServiceCostShares] is JsonObject serviceCostShares)
                    docs.Add(Doc(serviceCostShares, IdOf(serviceCostShares), planId, RelPlanServiceCostShares,
                                 parentId: planServiceId));
            }
        }

        return docs;
    }

    // Every objectId in the tree, in the same order Flatten produces. Used by DELETE, which
    // must know the ids before Redis drops them.
    public static IReadOnlyList<string> CollectIds(JsonObject plan) =>
        Flatten(plan).Select(doc => doc.Id).ToList();

    private static EsDocument Doc(JsonObject obj, string id, string routing, string relation, string? parentId)
    {
        var source = new JsonObject();

        // Scalars only. Every nested object and array becomes its own document, so copying
        // them here would duplicate child data into the parent and push a nested `copay`
        // into a mapping that declares copay as a flat integer.
        foreach (var (name, value) in obj)
            if (value is not JsonObject && value is not JsonArray)
                source[name] = value?.DeepClone();

        // Root has a relation name only; children also name their immediate parent.
        source["plan_join"] = parentId is null
            ? new JsonObject { ["name"] = relation }
            : new JsonObject { ["name"] = relation, ["parent"] = parentId };

        return new EsDocument(id, routing, source);
    }

    private static string IdOf(JsonObject obj) =>
        obj["objectId"]?.GetValue<string>()
        ?? throw new InvalidOperationException("cannot index an object without an objectId");
}
