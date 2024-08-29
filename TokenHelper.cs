using System;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace resource_inventory;
public static class TokenHelper
{
    private static string cachedToken = null;
    private static DateTimeOffset tokenExpiry = DateTimeOffset.MinValue;

    public static async Task<string> GetAccessToken()
    {
        // Attempt to retrieve the Managed Identity Client ID from environment variables
        string managedIdentityClientId = Environment.GetEnvironmentVariable("MANAGED_IDENTITY_CLIENT_ID");

        try
        {
            // Check if the managed identity client ID is set and attempt to use it
            if (!string.IsNullOrEmpty(managedIdentityClientId))
            {
                if (cachedToken != null && DateTimeOffset.UtcNow < tokenExpiry)
                {
                    return cachedToken;
                }

                var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
                {
                    ManagedIdentityClientId = managedIdentityClientId
                });

                var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
                AccessToken accessToken = await credential.GetTokenAsync(tokenRequestContext);

                cachedToken = accessToken.Token;
                tokenExpiry = accessToken.ExpiresOn;
                return cachedToken;
            }
            else
            {
                // If no managed identity, fallback to default credentials (for local development/testing)
                return await GetAccessTokenUsingDefaultCredentials();
            }
        }
        catch (Exception ex)
        {
            // Log the error if needed and fall back to using the default credentials
            Console.WriteLine($"Managed identity not found or error occurred: {ex.Message}. Falling back to default credentials.");

            // Fallback to default credentials
            return await GetAccessTokenUsingDefaultCredentials();
        }
    }

    private static async Task<string> GetAccessTokenUsingDefaultCredentials()
    {
        try
        {
            if (cachedToken != null && DateTimeOffset.UtcNow < tokenExpiry)
            {
                return cachedToken;
            }

            var credential = new DefaultAzureCredential();
            var tokenRequestContext = new TokenRequestContext(new[] { "https://management.azure.com/.default" });
            AccessToken accessToken = await credential.GetTokenAsync(tokenRequestContext);

            cachedToken = accessToken.Token;
            tokenExpiry = accessToken.ExpiresOn;
            return cachedToken;
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to acquire token using default credentials: {ex.Message}", ex);
        }
    }
}