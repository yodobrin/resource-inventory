
namespace resource_inventory;
public interface IGatewayFunction
{
    bool ValidateInputs(HttpRequest req, out string validationError);
    string BuildRequestUrl(string baseUrl, string identifier);
    Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken,ILogger log);
    string MergeResults(List<string> jsonResponses);
}