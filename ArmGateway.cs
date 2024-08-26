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
using System.Linq;

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
                // Retrieve the armRoute and resourceIds from query parameters
                string armRoute = req.Query["armRoute"];
                string resourceIdsParam = req.Query["resourceIds"];
                
                if (string.IsNullOrEmpty(armRoute))
                {
                    log.LogError("Missing armRoute query parameter");
                    return new BadRequestObjectResult("Missing armRoute query parameter");
                }

                if (string.IsNullOrEmpty(resourceIdsParam))
                {
                    log.LogError("Missing resourceIds query parameter");
                    return new BadRequestObjectResult("Missing resourceIds query parameter");
                }

                log.LogInformation($"ARM Route: {armRoute}");
                log.LogInformation($"Resource IDs: {resourceIdsParam}");

                // Extract parameter names from the armRoute
                List<string> paramList = ExtractParameterNames(armRoute);

                // Split the resourceIds into a list
                var resourceIds = resourceIdsParam.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();

                // Generate ARM routes by replacing markers with the corresponding values
                var armRoutes = new List<string>();
                foreach (var resourceId in resourceIds)
                {
                    var generatedRoute = ReplaceMarkersWithValues(armRoute, paramList, resourceId);
                    armRoutes.Add(generatedRoute);
                }

                string accessToken = await GetAccessToken();
                log.LogInformation("Successfully obtained access token");

                // Call the ARM API for each generated route
                var responseContent = await CallArmApiAndCombineResultsAsync(
                    armRoutes,
                    accessToken,
                    log
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

        // Step 1: Extract parameter names from armRoute
        private static List<string> ExtractParameterNames(string armRoute)
        {
            var parameterNames = new List<string>();
            var parts = armRoute.Split('/');

            foreach (var part in parts)
            {
                if (part.StartsWith("$"))
                {
                    // Add the parameter name without the $ prefix
                    parameterNames.Add(part.Substring(1));
                }
            }

            return parameterNames;
        }

        // Step 2: Replace markers in the armRoute with actual values from resourceId
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

        // Step 3: Call the ARM API and combine the results
        private static async Task<string> CallArmApiAndCombineResultsAsync(List<string> armRoutes, string accessToken, ILogger log)
        {
            var tasks = new List<Task<string>>();

            foreach (var route in armRoutes)
            {
                log.LogInformation($"Calling API with route: {route}");
                tasks.Add(CallArmApiAsync(route, accessToken, log));
            }

            // Wait for all tasks to complete
            var responses = await Task.WhenAll(tasks);

            // Merge the JSON results
            return MergeJsonResults(responses);
        }

        // Helper method to call the ARM API
        private static async Task<string> CallArmApiAsync(string fullUrl, string accessToken, ILogger log)
        {
            try
            {
                string armBaseUrl = "https://management.azure.com";
                string fullApiUrl = $"{armBaseUrl}{fullUrl}";
                log.LogInformation($"Calling ARM API: {fullApiUrl}");

                using HttpClient client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

                HttpResponseMessage response = await client.GetAsync(fullApiUrl);
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

        // Helper method to merge JSON results
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

        // Helper method to obtain an access token
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
}
