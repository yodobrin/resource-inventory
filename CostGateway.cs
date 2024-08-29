using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace resource_inventory;

public static class CostGateway
{
    [FunctionName("CostGateway")]
    public static async Task<IActionResult> Run(
        [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)] HttpRequest req,
        ILogger log)
    {
        log.LogInformation("Processing Cost Management API request.");

        try
        {
            // Get the query parameter
            string scopeParam = req.Query["scope"];
            if (string.IsNullOrEmpty(scopeParam))
            {
                return new BadRequestObjectResult("The 'scope' query parameter is required.");
            }

            // Read and parse the POST payload
            string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
            log.LogInformation($"Received payload: {requestBody}");

            // Get the access token
            string accessToken = await TokenHelper.GetAccessToken();
            log.LogInformation("Successfully obtained access token");

            // Split the scope parameter into individual scopes
            var scopes = scopeParam.Split(',', StringSplitOptions.RemoveEmptyEntries);

            // Prepare to send requests to each scope
            var tasks = new List<Task<string>>();
            foreach (var scope in scopes)
            {
                // Clean up the scope: remove single quotes and trim any leading/trailing slashes
                string cleanedScope = scope.Trim().Trim('\'').Trim('/');
                string costManagementUrl = $"https://management.azure.com/{cleanedScope}/providers/Microsoft.CostManagement/query?api-version=2023-11-01";
                
                log.LogInformation($"Preparing to call Cost Management API for scope: {cleanedScope} with URL: {costManagementUrl}");
                
                tasks.Add(CallCostManagementApiAsync(costManagementUrl, requestBody, accessToken, log));
            }

            // Wait for all requests to complete
            var responses = await Task.WhenAll(tasks);

            // Merge the JSON results
            var aggregatedResults = JsonHelper.MergeJsonResults(responses);

            log.LogInformation("Successfully merged all API responses.");
            return new OkObjectResult(aggregatedResults);
        }
        catch (Exception ex)
        {
            log.LogError($"An error occurred while processing the request: {ex.Message}");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static async Task<string> CallCostManagementApiAsync(string costManagementUrl, string payload, string accessToken, ILogger log)
    {
        try
        {
            using HttpClient client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            log.LogInformation($"Sending POST request to {costManagementUrl}");
            log.LogInformation($"Payload: {payload}");  // Log the payload before sending

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

            log.LogInformation($"Received successful response from {costManagementUrl}");
            return await response.Content.ReadAsStringAsync();
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