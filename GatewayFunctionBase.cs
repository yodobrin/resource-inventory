
namespace resource_inventory;
public abstract class GatewayFunctionBase : IGatewayFunction
{
    // Common method to get access token
    protected async Task<string> GetAccessTokenAsync(ILogger log)
    {
        return await TokenHelper.GetAccessToken(log);
    }

    // Default implementation for merging results (can be overridden)
    public virtual string MergeResults(List<string> jsonResponses)
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

    // Abstract methods that derived classes must implement
    public abstract bool ValidateInputs(HttpRequest req, out string validationError);
    public abstract string BuildRequestUrl(string baseUrl, string identifier);
    public abstract Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken,ILogger log);
}