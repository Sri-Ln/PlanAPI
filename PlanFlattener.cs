using System.Text.Json;
using System.Text.Json.Nodes;

namespace PlanApi;

public static class PlanFlattener
{
    // TODO: derive from schema instead of hardcoding (Demo 2 will add new object types)
    private static readonly HashSet<string> KnownObjectTypes = new()
    {
        "plan", "planservice", "service", "membercostshare"
    };

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

    // Read `key` via `read`, then inline every ref string back into a nested object.
    public static async Task<JsonObject?> AssembleAsync(string key, Func<string, Task<string?>> read)
    {
        var json = await read(key);
        if (json is null) return null;

        var flat = (JsonObject)JsonNode.Parse(json)!;
        var result = new JsonObject();

        foreach (var (name, value) in flat)
        {
            if (value is JsonValue jv && IsRefString(jv, out var refKey))
            {
                result[name] = await AssembleAsync(refKey, read);
            }
            else if (value is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue first && IsRefString(first, out _))
            {
                var inlined = new JsonArray();
                foreach (var item in arr)
                {
                    var itemKey = ((JsonValue)item!).GetValue<string>();
                    inlined.Add(await AssembleAsync(itemKey, read));
                }
                result[name] = inlined;
            }
            else
            {
                result[name] = value?.DeepClone();
            }
        }

        return result;
    }

    // Walk the same ref graph as AssembleAsync but only collect keys.
    public static async Task<IReadOnlyList<string>> CollectKeysAsync(string key, Func<string, Task<string?>> read)
    {
        var keys = new List<string>();
        await CollectInto(key, read, keys);
        return keys;
    }

    private static async Task CollectInto(string key, Func<string, Task<string?>> read, List<string> sink)
    {
        var json = await read(key);
        if (json is null) return;

        sink.Add(key);
        var flat = (JsonObject)JsonNode.Parse(json)!;

        foreach (var (_, value) in flat)
        {
            if (value is JsonValue jv && IsRefString(jv, out var refKey))
            {
                await CollectInto(refKey, read, sink);
            }
            else if (value is JsonArray arr && arr.Count > 0 && arr[0] is JsonValue first && IsRefString(first, out _))
            {
                foreach (var item in arr)
                    await CollectInto(((JsonValue)item!).GetValue<string>(), read, sink);
            }
        }
    }

    private static bool IsRefString(JsonValue value, out string refKey)
    {
        refKey = "";
        if (value.GetValueKind() != JsonValueKind.String) return false;
        var s = value.GetValue<string>();
        var colon = s.IndexOf(':');
        if (colon <= 0 || colon >= s.Length - 1) return false;
        if (!KnownObjectTypes.Contains(s[..colon])) return false;
        refKey = s;
        return true;
    }
}
