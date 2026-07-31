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
}
