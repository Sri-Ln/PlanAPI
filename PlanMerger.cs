using System.Text.Json.Nodes;

namespace PlanApi;

// Merges a partial PATCH body into a stored plan. Pure function, no I/O.
//   - object member with null value  -> remove it        (RFC 7386 merge-patch)
//   - nested object                  -> deep-merge recursively
//   - array of objects with objectId -> upsert by objectId (custom, not RFC 7386)
//   - anything else                  -> patch value overwrites
public static class PlanMerger
{
    public static JsonObject Merge(JsonObject target, JsonObject patch)
    {
        var result = (JsonObject)target.DeepClone();

        foreach (var (key, patchValue) in patch)
        {
            if (patchValue is null)
            {
                result.Remove(key);                       // null deletes the member
            }
            else if (patchValue is JsonObject patchObj && result[key] is JsonObject targetObj)
            {
                result[key] = Merge(targetObj, patchObj); // recurse into nested object
            }
            else if (patchValue is JsonArray patchArr && result[key] is JsonArray targetArr
                     && IsObjectArray(patchArr))
            {
                result[key] = MergeArrayByObjectId(targetArr, patchArr);
            }
            else
            {
                result[key] = patchValue.DeepClone();      // scalar / new key / type change / replaced array
            }
        }

        return result;
    }

    // Match items by objectId: existing ones deep-merged, unseen objectIds appended.
    private static JsonArray MergeArrayByObjectId(JsonArray target, JsonArray patch)
    {
        var result = (JsonArray)target.DeepClone();

        var indexById = new Dictionary<string, int>();
        for (var i = 0; i < result.Count; i++)
            if (result[i] is JsonObject o && o["objectId"] is JsonNode id)
                indexById[id.GetValue<string>()] = i;

        foreach (var item in patch)
        {
            if (item is JsonObject patchItem
                && patchItem["objectId"] is JsonNode pid
                && indexById.TryGetValue(pid.GetValue<string>(), out var idx))
            {
                result[idx] = Merge((JsonObject)result[idx]!, patchItem);
            }
            else
            {
                result.Add(item!.DeepClone());             // new objectId -> append
            }
        }

        return result;
    }

    private static bool IsObjectArray(JsonArray arr) =>
        arr.Count > 0 && arr[0] is JsonObject o && o["objectId"] is not null;
}
