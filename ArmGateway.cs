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
                // Retrieve the routeTemplate from query parameters
                string routeTemplate = req.Query["routeTemplate"];
                if (string.IsNullOrEmpty(routeTemplate))
                {
                    log.LogError("Missing routeTemplate query parameter");
                    return new BadRequestObjectResult("Missing routeTemplate query parameter");
                }
                log.LogInformation($"Route Template: {routeTemplate}");

                // Parse the query parameters into a dictionary of lists
                var parameterSets = req.Query
                    .Where(q => q.Key != "routeTemplate")
                    .ToDictionary(
                        q => q.Key,
                        q => q.Value.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                    );

                if (parameterSets.Count == 0)
                {
                    log.LogError("No parameters provided in the query string");
                    return new BadRequestObjectResult("No parameters provided in the query string");
                }

                // Log the passed parameters
                foreach (var param in parameterSets)
                {
                    log.LogInformation($"Parameter: {param.Key} - Values: {string.Join(", ", param.Value)}");
                }

                string accessToken = await GetAccessToken();
                log.LogInformation("Successfully obtained access token");

                // Call the ARM API for each parameter combination
                var responseContent = await CallApisForMultipleParameterSetsAsync(
                    routeTemplate,
                    parameterSets,
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

        private static async Task<string> CallApisForMultipleParameterSetsAsync(
            string routeTemplate,
            Dictionary<string, List<string>> parameterSets,
            string accessToken,
            ILogger log)
        {
            try
            {
                var tasks = new List<Task<string>>();

                // Combine the parameters into all possible combinations and call the API
                foreach (var paramSet in CombineParameterSets(parameterSets))
                {
                    // Log the parameter set being used for this API call
                    log.LogInformation($"Calling API with parameters: {string.Join(", ", paramSet.Select(kvp => $"{kvp.Key}={kvp.Value}"))}");
                    tasks.Add(CallArmApiAsync(routeTemplate, paramSet, accessToken, log));
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

        private static IEnumerable<Dictionary<string, string>> CombineParameterSets(Dictionary<string, List<string>> parameterSets)
        {
            var keys = parameterSets.Keys.ToList();
            var combinations = new List<Dictionary<string, string>>();

            void Combine(int index, Dictionary<string, string> current)
            {
                if (index == keys.Count)
                {
                    combinations.Add(new Dictionary<string, string>(current));
                    return;
                }

                var key = keys[index];
                foreach (var value in parameterSets[key])
                {
                    current[key] = value;
                    Combine(index + 1, current);
                }
            }

            Combine(0, new Dictionary<string, string>());
            return combinations;
        }

        private static async Task<string> CallArmApiAsync(string routeTemplate, Dictionary<string, string> parameters, string accessToken, ILogger log)
        {
            try
            {
                foreach (var param in parameters)
                {
                    routeTemplate = routeTemplate.Replace($"${param.Key}", param.Value);
                }

                string armBaseUrl = "https://management.azure.com";
                string fullUrl = $"{armBaseUrl}{routeTemplate}";
                log.LogInformation($"Calling ARM API: {fullUrl}");

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
}
