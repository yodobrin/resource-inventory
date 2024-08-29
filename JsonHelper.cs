using System;
using System.Collections.Generic;
using System.Text.Json;

namespace resource_inventory;

public static class JsonHelper
{
    public static string MergeJsonResults(string[] jsonResponses)
    {
        try
        {
            var allResults = new List<JsonElement>();

            foreach (var jsonResponse in jsonResponses)
            {
                var jsonDocument = JsonDocument.Parse(jsonResponse);
                allResults.Add(jsonDocument.RootElement.Clone());
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            return JsonSerializer.Serialize(new { value = allResults }, options);
        }
        catch (Exception ex)
        {
            throw new Exception($"An error occurred while merging JSON results: {ex.Message}", ex);
        }
    }
}