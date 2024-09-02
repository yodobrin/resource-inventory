using System.IO;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Text;

namespace resource_inventory;

public class CostGateway : GatewayFunctionBase
{
    // Static variable to hold all regex patterns
    private static readonly Dictionary<string, string> _patterns = new Dictionary<string, string>
    {
        { @"^/subscriptions/[^/]+/resourceGroups/[^/]+", "/subscriptions/LIST/resourceGroups/LIST" },
        { @"^/subscriptions/[^/]+", "/subscriptions/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/departments/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/departments/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/enrollmentAccounts/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/enrollmentAccounts/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/billingProfiles/[^/]+/invoiceSections/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/billingProfiles/LIST/invoiceSections/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/billingProfiles/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/billingProfiles/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+/customers/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST/customers/LIST" },
        { @"/providers/Microsoft.Billing/billingAccounts/[^/]+", "/providers/Microsoft.Billing/billingAccounts/LIST" },
        { @"/providers/Microsoft.Management/managementGroups/[^/]+", "/providers/Microsoft.Management/managementGroups/LIST" }
    };
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
                                   .Select(scope =>  "/" + scope.Trim().Trim('\'').Trim('/'))
                                   .ToList();

            // Fan out to call Cost Management API for each scope
            var jsonResponses = await gateway.ExecuteFanOutAsync(requestBody, scopes, accessToken, log);

            // Merge results into a single JSON response using the base class method
            string mergedResult = gateway.MergeResults(jsonResponses);
            // build the final response
            // the id is generated based on the scope, we would take the first one
            var aScope = scopes.First();

            // Return the response
            log.LogInformation($"Cost Management API request processed successfully. {aScope}");
            return new OkObjectResult(UpdateMergedJson(mergedResult,aScope));
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
        // the scope contains a leading slash, thats the reason we are not appending it to the base url
        return $"https://management.azure.com{scope}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
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
    // Merge results method (specific to CostGateway)
    public override string MergeResults(List<string> jsonResponses)
    {
        // Ensure all responses have the same structure
        JsonElement mergedProperties = default;
        List<JsonElement> mergedColumns = new List<JsonElement>();
        List<JsonElement> mergedRows = new List<JsonElement>();

        foreach (var jsonResponse in jsonResponses)
        {
            var jsonDocument = JsonDocument.Parse(jsonResponse);
            var properties = jsonDocument.RootElement.GetProperty("properties");

            if (mergedProperties.ValueKind == JsonValueKind.Undefined)
            {
                mergedProperties = properties.Clone();
                mergedColumns = properties.GetProperty("columns").EnumerateArray().ToList();
            }

            var rows = properties.GetProperty("rows").EnumerateArray();
            mergedRows.AddRange(rows);
        }

        // Create the final JSON structure
        var finalJsonDocument = new
        {
            id = (string)null,  // Placeholder, to be set by UpdateMergedJson
            name = (string)null,  // Placeholder, to be set by UpdateMergedJson
            type = "Microsoft.CostManagement/query",
            properties = new
            {
                columns = mergedColumns,
                rows = mergedRows
            }
        };

        // Serialize the object to JSON
        var options = new JsonSerializerOptions { WriteIndented = true };
        var finalJson = JsonSerializer.Serialize(finalJsonDocument, options);
        
        return finalJson;
    }
    // Supporting method to generate a generic ID
    private static string GenerateGenericId(string scope)
    {
        foreach (var pattern in _patterns)
        {
            if (Regex.IsMatch(scope, pattern.Key))
            {
                return Regex.Replace(scope, pattern.Key, pattern.Value);
            }
        }
        return scope; // Return the original scope if no pattern matched
    }

private static string UpdateMergedJson(string mergedJson, string scope)
{
    // Generate the generic ID based on the scope
    var genericId = GenerateGenericId(scope);
    var generatedGuid = Guid.NewGuid().ToString();

    // Parse the JSON to a JsonDocument to modify it
    using var document = JsonDocument.Parse(mergedJson);
    var root = document.RootElement;

    using var outputStream = new MemoryStream();
    using (var writer = new Utf8JsonWriter(outputStream, new JsonWriterOptions { Indented = true }))
    {
        writer.WriteStartObject(); // Start root object

        // Directly write the updated "id" and "name" fields
        writer.WriteString("id", $"{genericId}/{generatedGuid}");
        writer.WriteString("name", generatedGuid);

        // Write the other properties from the original JSON
        writer.WritePropertyName("type");
        root.GetProperty("type").WriteTo(writer);

        writer.WritePropertyName("properties");
        root.GetProperty("properties").WriteTo(writer);

        writer.WriteEndObject(); // End root object
    }

    return Encoding.UTF8.GetString(outputStream.ToArray());
}
}