using System.IO;
using System.Net.Http.Headers;

namespace resource_inventory;

public class CostGateway : GatewayFunctionBase
{
    [FunctionName("CostGateway")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Processing Cost Management API request.");
        var gateway = new CostGateway();

        // Validate inputs
        if (!gateway.ValidateInputs(req, out var validationError))
        {
            log.LogError($"Validation failed: {validationError}");
            return new BadRequestObjectResult(new { error = "Invalid inputs", details = validationError });
        }

        try
        {
            // Extract the scope and request body from the request
            string scopeParam = req.Query["scope"];
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();

            log.LogInformation($"Received payload: {requestBody}");
            log.LogInformation($"Scopes: {scopeParam}");

            // Retrieve the access token using the method from the base class
            string accessToken = await gateway.GetAccessTokenAsync(log);
            log.LogInformation("Successfully obtained and cached new token.");

            // Split the scope parameter into individual scopes
            var scopes = scopeParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                   .Select(scope => scope.Trim().Trim('\'').Trim('/'))
                                   .ToList();

            // Fan out to call Cost Management API for each scope
            var jsonResponses = await gateway.ExecuteFanOutAsync(requestBody, scopes, accessToken, log);

            // Merge results into a single JSON response using the base class method
            string mergedResult = gateway.MergeResults(jsonResponses);

            // Return the response
            log.LogInformation("Cost Management API request processed successfully.");
            return new OkObjectResult(mergedResult);
        }
        catch (Exception ex)
        {
            log.LogError($"An error occurred while processing the request: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    public override bool ValidateInputs(HttpRequest req, out string validationError)
    {
        validationError = string.Empty;
        if (string.IsNullOrEmpty(req.Query["scope"]))
        {
            validationError = "The 'scope' query parameter is required.";
            return false;
        }
        return true;
    }

     public override string BuildRequestUrl(string baseUrl, string scope)
    {
        return $"https://management.azure.com/{scope}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
    }

    public override async Task<List<string>> ExecuteFanOutAsync(string requestBody, List<string> scopes, string accessToken, ILogger log)
    {
        var tasks = scopes.Select(scope => CallCostManagementApiAsync(BuildRequestUrl(null, scope), requestBody, accessToken, log)).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<string> CallCostManagementApiAsync(string costManagementUrl, string payload, string accessToken, ILogger log)
    {
        try
        {
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            log.LogInformation($"Sending POST request to {costManagementUrl}");
            log.LogInformation($"Payload: {payload}");

            var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            HttpResponseMessage response = await client.PostAsync(costManagementUrl, content);

            if (!response.IsSuccessStatusCode)
            {
                string responseContent = await response.Content.ReadAsStringAsync();
                log.LogError($"HTTP request error while calling Cost Management API for URL '{costManagementUrl}': " +
                            $"Status Code: {response.StatusCode} ({(int)response.StatusCode}), " +
                            $"Reason: {response.ReasonPhrase}, " +
                            $"Response: {responseContent}");

                throw new HttpRequestException($"Failed to call Cost Management API. " +
                                            $"Status Code: {response.StatusCode} ({(int)response.StatusCode}), " +
                                            $"Reason: {response.ReasonPhrase}, " +
                                            $"Response: {responseContent}");
            }
            var costResponse = await response.Content.ReadAsStringAsync();
            log.LogInformation($"Received successful response from {costManagementUrl} : {costResponse}");
            return costResponse;
        }
        catch (HttpRequestException ex)
        {
            log.LogError($"HTTP request exception: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            log.LogError($"An unexpected error occurred while calling Cost Management API for URL '{costManagementUrl}': {ex.Message}");
            throw;
        }
    }
}