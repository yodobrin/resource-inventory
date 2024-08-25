using System.IO;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using Azure.Identity;
using Azure.Core;
using System;

namespace resource_inventory
{
    public static class ArmGateway
    {
        [FunctionName("ArmGateway")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "get", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Processing ARM API request.");

            try
            {
                // Read and parse the request body
                string requestBody = await new StreamReader(req.Body).ReadToEndAsync();
                log.LogInformation($"Got request: {requestBody}");

                var armApiRequest = JsonSerializer.Deserialize<ArmApiRequest>(requestBody);
                log.LogInformation($"Parsed request route: {armApiRequest.RouteTemplate}");

                // Validate the request
                if (string.IsNullOrEmpty(armApiRequest.RouteTemplate) || armApiRequest.ParameterSets == null)
                {
                    log.LogError("Invalid request payload");
                    return new BadRequestObjectResult("Invalid request payload");
                }

                string accessToken = await GetAccessToken();
                log.LogInformation("Successfully obtained access token");

                // Call the ARM API for each parameter set
                var responseContent = await CallApisForMultipleParameterSetsAsync(
                    armApiRequest.RouteTemplate,
                    armApiRequest.ParameterSets,
                    accessToken
                );

                log.LogInformation("Successfully processed all API requests");
                return new OkObjectResult(responseContent);
            }
            catch (Exception ex)
            {
                log.LogError($"An error occurred while processing the request: {ex.Message}");
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }
        }

        private static async Task<string> CallApisForMultipleParameterSetsAsync(
            string routeTemplate,
            List<Dictionary<string, string>> parameterSets,
            string accessToken)
        {
            try
            {
                var tasks = new List<Task<string>>();

                foreach (var parameters in parameterSets)
                {
                    tasks.Add(CallArmApiAsync(routeTemplate, parameters, accessToken));
                }

                // Wait for all tasks to complete
                var responses = await Task.WhenAll(tasks);

                // Merge the JSON results
                return MergeJsonResults(responses);
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while calling multiple ARM API endpoints: {ex.Message}", ex);
            }
        }

        private static async Task<string> CallArmApiAsync(string routeTemplate, Dictionary<string, string> parameters, string accessToken)
        {
            try
            {
                foreach (var param in parameters)
                {
                    routeTemplate = routeTemplate.Replace($"{{{param.Key}}}", param.Value);
                }

                string armBaseUrl = "https://management.azure.com";
                string fullUrl = $"{armBaseUrl}{routeTemplate}";

                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                HttpResponseMessage response = await client.GetAsync(fullUrl);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsStringAsync();
            }
            catch (HttpRequestException ex)
            {
                throw new Exception($"HTTP request error while calling ARM API: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"An unexpected error occurred while calling ARM API: {ex.Message}", ex);
            }
        }

        private static string MergeJsonResults(string[] jsonResponses)
        {
            try
            {
                var allResults = new List<JsonElement>();

                foreach (var jsonResponse in jsonResponses)
                {
                    var jsonDocument = JsonDocument.Parse(jsonResponse);
                    allResults.Add(jsonDocument.RootElement.Clone());
                }

                using var jsonDocumentTemplate = JsonDocument.Parse("{\"value\": []}");
                var root = jsonDocumentTemplate.RootElement.Clone();
                var array = root.GetProperty("value").EnumerateArray();

                var mergedArray = new List<JsonElement>(array);

                foreach (var result in allResults)
                {
                    mergedArray.AddRange(result.GetProperty("value").EnumerateArray());
                }

                var options = new JsonSerializerOptions { WriteIndented = true };
                return JsonSerializer.Serialize(new { value = mergedArray }, options);
            }
            catch (Exception ex)
            {
                throw new Exception($"An error occurred while merging JSON results: {ex.Message}", ex);
            }
        }

        private static async Task<string> GetAccessToken()
        {
            try
            {
                // Retrieve the Managed Identity Client ID from environment variables
                string managedIdentityClientId = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");

                if (string.IsNullOrEmpty(managedIdentityClientId))
                {
                    throw new Exception("Managed Identity Client ID is not set in the environment variables.");
                }

                // Initialize the DefaultAzureCredential with the ManagedIdentityClientId
                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = managedIdentityClientId
                });

                var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
                AccessToken accessToken = await credential.GetTokenAsync(tokenRequestContext);

                return accessToken.Token;
            }
            catch (AuthenticationFailedException ex)
            {
                throw new Exception($"Authentication failed: {ex.Message}", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"An unexpected error occurred while obtaining the access token: {ex.Message}", ex);
            }
        }

    }

    public class ArmApiRequest
    {
        [JsonPropertyName("routeTemplate")]
        public string RouteTemplate { get; set; }

        [JsonPropertyName("parameterSets")]
        public List<Dictionary<string, string>> ParameterSets { get; set; }
    }
}

