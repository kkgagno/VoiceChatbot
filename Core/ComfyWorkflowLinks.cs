using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace VoiceChatbot;

/// <summary>Where a UI-workflow link comes from. <paramref name="Type"/> is the link's data type (MODEL, IMAGE, ...).</summary>
public sealed record ComfyWorkflowLink(string OriginNodeId, int OriginSlot, string Type = "");

/// <summary>
/// Link handling for converting a ComfyUI UI workflow (nodes + links) to an API prompt, following
/// the ComfyUI frontend: reroutes are skipped, bypassed nodes pass a matching input straight
/// through, and muted nodes are left out together with every input that depends on them.
/// </summary>
public static class ComfyWorkflowLinks
{
    /// <summary>LiteGraph "never" mode: the node is muted and does not run.</summary>
    public const int ModeMuted = 2;

    /// <summary>ComfyUI bypass mode: the node is skipped and its inputs are passed through.</summary>
    public const int ModeBypass = 4;

    public static bool IsBypassed(JsonObject node) => TryGetInt(node["mode"]) == ModeBypass;

    /// <summary>Muted and bypassed nodes are not part of the API prompt.</summary>
    public static bool IsExcluded(JsonObject node) => TryGetInt(node["mode"]) is ModeMuted or ModeBypass;

    /// <summary>
    /// Maps link id to its effective origin. Links through reroutes and bypassed nodes point at the
    /// real upstream node; a link through a bypassed node with no matching connected input is dropped.
    /// </summary>
    public static Dictionary<int, ComfyWorkflowLink> BuildLinkMap(JsonArray? links, JsonArray? nodes = null)
    {
        var map = new Dictionary<int, ComfyWorkflowLink>();
        if (links is null)
            return map;

        foreach (var link in links)
        {
            if (link is JsonArray arr && arr.Count >= 4)
            {
                // [id, origin_id, origin_slot, target_id, target_slot, type]
                var linkId = TryGetInt(arr[0]);
                var originNodeId = TryGetInt(arr[1]);
                var originSlot = TryGetInt(arr[2]) ?? 0;
                if (linkId.HasValue && originNodeId.HasValue)
                    map[linkId.Value] = new ComfyWorkflowLink(originNodeId.Value.ToString(), originSlot, arr.Count > 5 ? GetString(arr[5]) : "");
                continue;
            }

            if (link is JsonObject obj)
            {
                var linkId = TryGetInt(obj["id"]);
                var originNodeId = TryGetInt(obj["origin_id"]);
                var originSlot = TryGetInt(obj["origin_slot"]) ?? 0;
                if (linkId.HasValue && originNodeId.HasValue)
                    map[linkId.Value] = new ComfyWorkflowLink(originNodeId.Value.ToString(), originSlot, GetString(obj["type"]));
            }
        }

        ResolveRerouteLinks(map, nodes);
        ResolveBypassedLinks(map, nodes);
        return map;
    }

    /// <summary>
    /// Removes inputs that link to a node missing from the prompt (a muted node, or a reroute or
    /// bypass with nothing connected), as the ComfyUI frontend does. Returns how many were removed.
    /// </summary>
    public static int RemoveDanglingLinks(Dictionary<string, object> prompt)
    {
        var removed = 0;
        foreach (var node in prompt.Values.OfType<Dictionary<string, object>>())
        {
            if (!node.TryGetValue("inputs", out var inputsObj) || inputsObj is not Dictionary<string, object> inputs)
                continue;

            foreach (var key in inputs.Keys.ToList())
            {
                if (inputs[key] is object[] { Length: 2 } link
                    && link[0] is string originId
                    && !prompt.ContainsKey(originId))
                {
                    inputs.Remove(key);
                    removed++;
                }
            }
        }

        return removed;
    }

    public static string GetNodeId(JsonObject node)
    {
        if (node["id"] is JsonValue idValue)
        {
            if (idValue.TryGetValue<int>(out var id))
                return id.ToString();
            if (idValue.TryGetValue<string>(out var text))
                return text;
        }

        return "";
    }

    public static int? TryGetInt(JsonNode? node)
    {
        if (node is not JsonValue value)
            return null;
        if (value.TryGetValue<int>(out var intValue))
            return intValue;
        if (value.TryGetValue<long>(out var longValue))
            return (int)longValue;
        if (value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed))
            return parsed;
        return null;
    }

    private static void ResolveRerouteLinks(Dictionary<int, ComfyWorkflowLink> linksById, JsonArray? nodes)
    {
        if (nodes is null)
            return;

        var rerouteInputLinks = new Dictionary<string, int>();
        foreach (var node in nodes.OfType<JsonObject>())
        {
            var nodeId = GetNodeId(node);
            var classType = GetString(node["type"]);
            if (string.IsNullOrWhiteSpace(nodeId) || !classType.Equals("Reroute", StringComparison.OrdinalIgnoreCase))
                continue;

            var inputLink = (node["inputs"] as JsonArray)?
                .OfType<JsonObject>()
                .Select(input => TryGetInt(input["link"]))
                .FirstOrDefault(id => id.HasValue);
            if (inputLink.HasValue)
                rerouteInputLinks[nodeId] = inputLink.Value;
        }

        foreach (var linkId in linksById.Keys.ToList())
            linksById[linkId] = ResolveRerouteOrigin(linksById[linkId], linksById, rerouteInputLinks, new HashSet<string>());
    }

    private static ComfyWorkflowLink ResolveRerouteOrigin(
        ComfyWorkflowLink link,
        Dictionary<int, ComfyWorkflowLink> linksById,
        Dictionary<string, int> rerouteInputLinks,
        HashSet<string> seen)
    {
        if (!rerouteInputLinks.TryGetValue(link.OriginNodeId, out var inputLinkId) || !seen.Add(link.OriginNodeId))
            return link;

        return linksById.TryGetValue(inputLinkId, out var previous)
            ? ResolveRerouteOrigin(previous, linksById, rerouteInputLinks, seen)
            : link;
    }

    private static void ResolveBypassedLinks(Dictionary<int, ComfyWorkflowLink> linksById, JsonArray? nodes)
    {
        if (nodes is null)
            return;

        var bypassed = new Dictionary<string, JsonObject>();
        foreach (var node in nodes.OfType<JsonObject>())
        {
            var nodeId = GetNodeId(node);
            if (!string.IsNullOrWhiteSpace(nodeId) && IsBypassed(node))
                bypassed[nodeId] = node;
        }

        if (bypassed.Count == 0)
            return;

        var original = new Dictionary<int, ComfyWorkflowLink>(linksById);
        foreach (var (linkId, link) in original)
        {
            var resolved = ResolveThroughBypassed(link, original, bypassed);
            if (resolved is null)
                linksById.Remove(linkId);
            else
                linksById[linkId] = resolved;
        }
    }

    // Walks upstream while the origin is bypassed. The type asked for is the one the link carries
    // to its consumer, as in the frontend's bypass handling.
    private static ComfyWorkflowLink? ResolveThroughBypassed(
        ComfyWorkflowLink link,
        Dictionary<int, ComfyWorkflowLink> linksById,
        Dictionary<string, JsonObject> bypassed)
    {
        var seen = new HashSet<string>();
        var current = link;
        while (bypassed.TryGetValue(current.OriginNodeId, out var node))
        {
            if (!seen.Add(current.OriginNodeId))
                return null;

            var inputs = node["inputs"] as JsonArray;
            var inputIndex = FindBypassInput(node, inputs, current.OriginSlot, link.Type);
            if (inputIndex < 0)
                return null;

            var upstreamLinkId = TryGetInt((inputs![inputIndex] as JsonObject)?["link"]);
            if (!upstreamLinkId.HasValue || !linksById.TryGetValue(upstreamLinkId.Value, out var upstream))
                return null;

            current = upstream;
        }

        return current;
    }

    // Prefers the input at the same index as the output, if its type fits, then the first input of
    // the same type, then the first compatible one. -1 when nothing matches.
    private static int FindBypassInput(JsonObject node, JsonArray? inputs, int outputSlot, string type)
    {
        if (inputs is null || inputs.Count == 0)
            return -1;

        if (IsAnyType(type))
            return outputSlot >= 0 && outputSlot < inputs.Count ? outputSlot : 0;

        var outputType = GetString((node["outputs"] as JsonArray)?.ElementAtOrDefault(outputSlot)?["type"]);
        if (outputType.Length == 0)
            outputType = type;
        if (outputSlot >= 0 && outputSlot < inputs.Count && TypesCompatible(InputType(inputs[outputSlot]), outputType))
            return outputSlot;

        for (var i = 0; i < inputs.Count; i++)
        {
            if (string.Equals(InputType(inputs[i]), type, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        for (var i = 0; i < inputs.Count; i++)
        {
            if (TypesCompatible(InputType(inputs[i]), type))
                return i;
        }

        return -1;
    }

    private static string InputType(JsonNode? input) => GetString(input?["type"]);

    private static bool IsAnyType(string type) => type.Length == 0 || type == "*";

    private static bool TypesCompatible(string a, string b)
    {
        if (IsAnyType(a) || IsAnyType(b))
            return true;

        var left = a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var right = b.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return left.Any(l => right.Contains(l, StringComparer.OrdinalIgnoreCase));
    }

    private static string GetString(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : "";
}
