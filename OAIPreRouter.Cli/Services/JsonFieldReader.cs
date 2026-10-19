namespace OAIPreRouter.Cli.Services;

using System.Text.Json;

public static class JsonFieldReader
{
    public static string? TryReadTopLevelString(string json, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty(fieldName, out var prop))
                return null;

            return prop.ValueKind == JsonValueKind.String ? prop.GetString() : prop.ToString();
        }
        catch
        {
            return null;
        }
    }

    public static bool? TryReadTopLevelBool(string json, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty(fieldName, out var prop))
                return null;

            return prop.ValueKind is JsonValueKind.True or JsonValueKind.False ? prop.GetBoolean() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a top-level numeric sampling field (e.g. "temperature", "top_p") for
    /// rewrite logging. Returns null when the field is absent, not a number, or the
    /// body is not parseable JSON — the caller logs "kept" in the first two cases and
    /// "parse-fail" in the last.
    /// </summary>
    public static double? TryReadTopLevelNumber(string json, string fieldName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (!doc.RootElement.TryGetProperty(fieldName, out var prop))
                return null;

            return prop.ValueKind == JsonValueKind.Number ? prop.GetDouble() : null;
        }
        catch
        {
            return null;
        }
    }
}
