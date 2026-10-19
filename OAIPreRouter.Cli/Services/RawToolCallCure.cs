using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OAIPreRouter.Cli.Services;

// Adapted from 0rand/opencode_compat_proxy. See THIRD-PARTY-NOTICES.md.
public static class RawToolCallCure
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static MatchCollection Matches(string text, string pattern) =>
        Regex.Matches(text, pattern, RegexOptions.Singleline, RegexTimeout);

    private const string NamespacePrefix = @"(?:[|\uFF5C]DSML[|\uFF5C]\s*|DSML[:\s]+|DSML>)";

    private static string Normalize(string text) => Regex.Replace(text,
        // Parameter bodies are data, including whitespace, entities and literal DSML text.
        @"(?<open><" + NamespacePrefix + @"?parameter\b[^>]*>)(?<value>.*?)(?<close></" + NamespacePrefix + @"?parameter\s*>)|" +
        @"<(?<slash>/?)" + NamespacePrefix + @"(?<tag>calls|tool_calls|invoke|parameter)\b",
        m => m.Groups["value"].Success
            ? Normalize(m.Groups["open"].Value) + m.Groups["value"].Value + Normalize(m.Groups["close"].Value)
            : "<" + m.Groups["slash"].Value + (m.Groups["tag"].Value == "calls" ? "tool_calls" : m.Groups["tag"].Value),
        RegexOptions.Singleline, RegexTimeout);

    // These prefixes also cover markers split at any character boundary.
    private static readonly string[] Starts = ["<|DSML|", "<\uFF5CDSML\uFF5C", "<DSML", "<tool_calls>", "<tool_call>"];

    public static int FindStart(string text)
    {
        var positions = Starts.Select(s => text.IndexOf(s, StringComparison.Ordinal)).Where(i => i >= 0);
        return positions.DefaultIfEmpty(-1).Min();
    }

    public static int PartialSuffixLength(string text)
    {
        var length = 0;
        foreach (var marker in Starts)
            for (var i = 1; i < marker.Length && i <= text.Length; i++)
                if (text.EndsWith(marker[..i], StringComparison.Ordinal)) length = Math.Max(length, i);
        return length;
    }

    public static bool TryParse(string text, ISet<string> allowedNames, out JsonArray calls, out string content)
    {
        calls = new JsonArray();
        var parsedCalls = calls;
        content = text;
        var normalized = Normalize(text);
        var blocks = Matches(normalized, @"<(?<tag>tool_calls|tool_call)>(?<body>.*?)</\k<tag>\s*>");
        if (blocks.Count == 0) return false;
        foreach (Match block in blocks)
        {
            var body = block.Groups["body"].Value;
            var invokes = Matches(body, "<invoke\\s+name=\"([^\"]+)\"\\s*>(.*?)</invoke\\s*>");
            if (invokes.Count > 0)
            {
                var remainder = body;
                foreach (Match invoke in invokes.Cast<Match>().Reverse()) remainder = remainder.Remove(invoke.Index, invoke.Length);
                if (!string.IsNullOrWhiteSpace(remainder)) return false;
                foreach (Match invoke in invokes)
                {
                    var args = new JsonObject();
                    var parameters = invoke.Groups[2].Value;
                    var matches = Matches(parameters, "<parameter\\s+name=\"([^\"]+)\"(?:\\s+string=\"(true|false)\")?\\s*>(.*?)</parameter\\s*>");
                    remainder = parameters;
                    foreach (Match parameter in matches.Cast<Match>().Reverse()) remainder = remainder.Remove(parameter.Index, parameter.Length);
                    if (!string.IsNullOrWhiteSpace(remainder)) return false;
                    foreach (Match parameter in matches)
                    {
                        var name = parameter.Groups[1].Value;
                        if (args.ContainsKey(name)) return false;
                        var value = parameter.Groups[3].Value;
                        try { args[name] = parameter.Groups[2].Value == "false" ? JsonNode.Parse(value) : JsonValue.Create(value); }
                        catch (JsonException) { return false; }
                    }
                    if (!Add(invoke.Groups[1].Value, args)) return false;
                }
            }
            else
            {
                var named = Matches(body, @"\s*<name>\s*(.*?)\s*</name>\s*<parameters>(.*?)</parameters>\s*");
                if (named.Count != 1 || named[0].Length != body.Length) return false;
                try
                {
                    if (JsonNode.Parse(System.Net.WebUtility.HtmlDecode(named[0].Groups[2].Value)) is not JsonObject args ||
                        !Add(named[0].Groups[1].Value, args)) return false;
                }
                catch (JsonException) { return false; }
            }
        }
        // Strip only validated blocks; retain surrounding assistant prose.
        foreach (Match block in blocks.Cast<Match>().Reverse()) normalized = normalized.Remove(block.Index, block.Length);
        if (FindStart(normalized) >= 0) return false;
        content = normalized;
        return calls.Count > 0;

        bool Add(string name, JsonObject args)
        {
            if (!allowedNames.Contains(name)) return false;
            parsedCalls.Add(new JsonObject
            {
                ["id"] = "call_" + Guid.NewGuid().ToString("N")[..24], ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args.ToJsonString() }
            });
            return true;
        }
    }

    public static HashSet<string> AllowedNames(string request)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (JsonNode.Parse(request) is not JsonObject root) return names;
            if (root?["tool_choice"]?.ToJsonString() == "\"none\"") return names;
            if (root?["tools"] is JsonArray tools)
                foreach (var tool in tools)
                    if (tool?["function"]?["name"]?.GetValue<string>() is string name) names.Add(name);
            if (root?["tool_choice"] is JsonObject choice && choice["function"]?["name"]?.GetValue<string>() is string forced)
                names.IntersectWith([forced]);
        }
        catch (JsonException) { }
        return names;
    }

    public static string RewriteJson(string json, ISet<string> allowedNames)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return json;
            if (root?["choices"] is not JsonArray choices) return json;
            var changed = false;
            foreach (var choice in choices)
            {
                if (choice?["message"] is not JsonObject message || message["tool_calls"] is JsonArray { Count: > 0 } ||
                    choice["finish_reason"]?.GetValue<string>() is not ("stop" or "tool_calls") ||
                    message["content"] is not JsonValue value || !value.TryGetValue<string>(out var text)) continue;
                if (!TryParse(text, allowedNames, out var calls, out var content)) continue;
                message["content"] = content.Length == 0 ? null : JsonValue.Create(content);
                message["tool_calls"] = calls;
                choice["finish_reason"] = "tool_calls";
                changed = true;
            }
            return changed ? root!.ToJsonString() : json;
        }
        catch (JsonException) { return json; }
    }
}
