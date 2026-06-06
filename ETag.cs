using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PlanApi;

public static class ETag
{
    // Returns a quoted strong ETag, e.g. "\"3a8f...\""
    public static string Compute(JsonNode node)
    {
        var canonical = Canonicalize(node)!.ToJsonString();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"\"{Convert.ToHexString(hash).ToLowerInvariant()}\"";
    }

    // Recursively rebuild the tree with object keys sorted (ordinal, case-sensitive).
    // Arrays keep their order — array position is semantic in JSON.
    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is null) return null;
        return node switch
        {
            JsonObject obj => new JsonObject(
                obj.OrderBy(kvp => kvp.Key, StringComparer.Ordinal)
                   .Select(kvp => KeyValuePair.Create(kvp.Key, Canonicalize(kvp.Value)))),
            JsonArray arr => new JsonArray(arr.Select(Canonicalize).ToArray()),
            _ => node.DeepClone()
        };
    }
}
