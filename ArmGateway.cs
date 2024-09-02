using System.Net.Http.Headers;

namespace resource_inventory;

public class ArmGateway : GatewayFunctionBase
{
    [FunctionName("ArmGateway")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Processing ARM API request.");
        var gateway = new ArmGateway();

        // Validate inputs
        if (!gateway.ValidateInputs(req, out var validationError))
        {
            log.LogError($"Validation failed: {validationError}");
            return new BadRequestObjectResult(new { error = "Invalid inputs", details = validationError });
        }

        try
        {
            // Extract the ARM route and resource IDs from the request
            string armRoute = req.Query["armRoute"];
            string resourceIdsParam = req.Query["resourceIds"];
            var resourceIds = resourceIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries)
                                              .Select(id => id.Trim('\'')).ToList();

            log.LogInformation($"ARM Route: {armRoute}");
            log.LogInformation($"Resource IDs: {string.Join(", ", resourceIds)}");

            // Retrieve the access token using the method from the base class
            string accessToken = await gateway.GetAccessTokenAsync(log);
            log.LogInformation("Successfully obtained and cached new token.");

            // Fan out to call ARM API for each resource ID
            var jsonResponses = await gateway.ExecuteFanOutAsync(armRoute, resourceIds, accessToken, log);

            // Merge results into a single JSON response using the base class method
            string mergedResult = gateway.MergeResults(jsonResponses);

            // Return the response
            log.LogInformation("ARM API request processed successfully.");
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
        if (string.IsNullOrEmpty(req.Query["armRoute"]) || string.IsNullOrEmpty(req.Query["resourceIds"]))
        {
            validationError = "Both 'armRoute' and 'resourceIds' query parameters are required.";
            return false;
        }
        return true;
    }

    public override string BuildRequestUrl(string armRoute, string resourceId)
    {
        var paramNames = ExtractParameterNames(armRoute);
        return ReplaceMarkersWithValues(armRoute, paramNames, resourceId);
    }

    public override async Task<List<string>> ExecuteFanOutAsync(string armRoute, List<string> resourceIds, string accessToken, ILogger log)
    {
        var tasks = resourceIds.Select(id => CallArmApiAsync(BuildRequestUrl(armRoute, id), accessToken, log)).ToList();
        return (await Task.WhenAll(tasks)).ToList();
    }

    private static async Task<string> CallArmApiAsync(string routeWithValues, string accessToken, ILogger log)
    {
        try
        {
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

            // Log the full URL before making the request
            string fullUrl = $"https://management.azure.com{routeWithValues}";
            log.LogInformation($"Calling ARM API: {fullUrl}");

            HttpResponseMessage response = await client.GetAsync(fullUrl);
            response.EnsureSuccessStatusCode();

            string content = await response.Content.ReadAsStringAsync();
            log.LogInformation($"ARM API response: {content}");

            return content;
        }
        catch (HttpRequestException ex)
        {
            log.LogError($"HTTP request error while calling ARM API: {ex.Message}");
            throw new Exception($"HTTP request error while calling ARM API: {ex.Message}", ex);
        }
        catch (Exception ex)
        {
            log.LogError($"An unexpected error occurred while calling ARM API: {ex.Message}");
            throw new Exception($"An unexpected error occurred while calling ARM API: {ex.Message}", ex);
        }
    }

    private static List<string> ExtractParameterNames(string armRoute)
    {
        var parameterNames = new List<string>();

        // Split the route into path and query parts
        var routeParts = armRoute.Split('?');

        // Handle the path part
        var pathParts = routeParts[0].Split('/');
        foreach (var part in pathParts)
        {
            if (part.StartsWith("$"))
            {
                // Add the parameter name without the $ prefix
                parameterNames.Add(part.Substring(1));
            }
        }
        return parameterNames;
    }

    private static string ReplaceMarkersWithValues(string armRoute, List<string> parameterNames, string resourceId)
    {
        // Split the resourceId into its components based on '/'
        var resourceParts = resourceId.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Replace the markers in the armRoute with the actual values from the resourceId
        foreach (var paramName in parameterNames)
        {
            // Find the index of the parameter name in the resourceId
            int index = Array.IndexOf(resourceParts, paramName);

            // Ensure that there is a corresponding value after the parameter name
            if (index >= 0 && index < resourceParts.Length - 1)
            {
                var valueToReplace = resourceParts[index + 1];
                var marker = $"${paramName}";
                armRoute = armRoute.Replace(marker, valueToReplace);
            }
            else
            {
                throw new Exception($"The parameter {paramName} does not have a corresponding value in the resourceId {resourceId}.");
            }
        }

        return armRoute;
    }
}