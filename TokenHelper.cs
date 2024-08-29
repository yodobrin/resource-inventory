using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace resource_inventory;

public static class TokenHelper
{
    private static string cachedToken = null;
    private static DateTimeOffset tokenExpiry = DateTimeOffset.MinValue;

    public static async Task<string> GetAccessToken(ILogger log = null)
    {
        // Attempt to retrieve the Managed Identity Client ID from environment variables
        string managedIdentityClientId = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");

        try
        {
            if (!string.IsNullOrEmpty(managedIdentityClientId))
            {
                return await GetTokenUsingManagedIdentity(managedIdentityClientId, log);
            }
            else
            {
                return await GetTokenUsingDefaultCredentials(log);
            }
        }
        catch (Exception ex)
        {
            log?.LogError($"Managed identity not found or error occurred: {ex.Message}. Falling back to default credentials.");
            return await GetTokenUsingDefaultCredentials(log);
        }
    }

    private static async Task<string> GetTokenUsingManagedIdentity(string clientId, ILogger log = null)
    {
        if (IsTokenValid())
        {
            log?.LogInformation("Returning cached token.");
            return cachedToken;
        }

        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = clientId
        });

        return await GetTokenAsync(credential, log);
    }

    private static async Task<string> GetTokenUsingDefaultCredentials(ILogger log = null)
    {
        if (IsTokenValid())
        {
            log?.LogInformation("Returning cached token.");
            return cachedToken;
        }

        var credential = new DefaultAzureCredential();
        return await GetTokenAsync(credential, log);
    }

    private static bool IsTokenValid()
    {
        return cachedToken != null && DateTimeOffset.UtcNow < tokenExpiry;
    }

    private static async Task<string> GetTokenAsync(TokenCredential credential, ILogger log = null)
    {
        try
        {
            var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
            AccessToken accessToken = await credential.GetTokenAsync(tokenRequestContext, CancellationToken.None);
          
            cachedToken = accessToken.Token;
            tokenExpiry = accessToken.ExpiresOn;

            log?.LogInformation("Successfully obtained and cached new token.");
            return cachedToken;
        }
        catch (Exception ex)
        {
            log?.LogError($"Failed to acquire token: {ex.Message}");
            throw new Exception($"Failed to acquire token: {ex.Message}", ex);
        }
    }

    public static void ClearCache()
    {
        cachedToken = null;
        tokenExpiry = DateTimeOffset.MinValue;
    }
}