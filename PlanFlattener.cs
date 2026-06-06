using System.Text.Json.Nodes;

namespace PlanApi;

public static class PlanFlattener
{
    // Walk the tree, return one flattened JsonObject per nested object.
    public static IReadOnlyDictionary<string, JsonObject> Decompose(JsonNode root)
    {
        var sink = new Dictionary<string, JsonObject>();
        DecomposeNode(root, sink);
        return sink;
    }

    // Returns the key under which `node` was stored, so callers can use it as a ref.
    private static string DecomposeNode(JsonNode node, Dictionary<string, JsonObject> sink)
    {
        var obj = node.AsObject();
        var key = $"{obj["objectType"]!.GetValue<string>()}:{obj["objectId"]!.GetValue<string>()}";

        var flat = new JsonObject();
        foreach (var (name, value) in obj)
        {
            flat[name] = value switch
            {
                JsonObject child when IsDecomposable(child) => DecomposeNode(child, sink),
                JsonArray arr                               => FlattenArray(arr, sink),
                _                                           => value?.DeepClone()
            };
        }

        sink[key] = flat;
        return key;
    }

    private static JsonArray FlattenArray(JsonArray arr, Dictionary<string, JsonObject> sink)
    {
        var refs = new JsonArray();
        foreach (var item in arr)
        {
            refs.Add(item is JsonObject child && IsDecomposable(child)
                ? DecomposeNode(child, sink)
                : item?.DeepClone());
        }
        return refs;
    }

    private static bool IsDecomposable(JsonObject obj) =>
        obj["objectType"] is not null && obj["objectId"] is not null;
}
