#!/bin/bash

# Variables
RESOURCE_GROUP=$1
LOCATION="EastUS"
FUNCTION_APP_NAME="myFunctionApp-$(openssl rand -hex 5)"
MANAGED_IDENTITY_NAME="myManagedIdentity-$(openssl rand -hex 5)"
STORAGE_ACCOUNT_NAME="mystorage$(openssl rand -hex 5)"
ZIP_URL="https://github.com/yodobrin/resource-inventory/releases/download/v1.0.2/functionapp.zip"  

# Check if resource group is provided
if [ -z "$RESOURCE_GROUP" ]; then
  echo "Usage: $0 <resource-group-name>"
  exit 1
fi

# Create resource group
echo "Creating resource group..."
az group create --name $RESOURCE_GROUP --location $LOCATION

# Create storage account for function app
echo "Creating storage account..."
az storage account create --name $STORAGE_ACCOUNT_NAME --location $LOCATION --resource-group $RESOURCE_GROUP --sku Standard_LRS

# Create a User-Assigned Managed Identity
echo "Creating User-Assigned Managed Identity..."
az identity create --name $MANAGED_IDENTITY_NAME --resource-group $RESOURCE_GROUP --location $LOCATION

# Get the full resource ID, Client ID, and Principal ID of the managed identity
MANAGED_IDENTITY_RESOURCE_ID=$(az identity show --name $MANAGED_IDENTITY_NAME --resource-group $RESOURCE_GROUP --query id -o tsv)
MANAGED_IDENTITY_CLIENT_ID=$(az identity show --name $MANAGED_IDENTITY_NAME --resource-group $RESOURCE_GROUP --query clientId -o tsv)
MANAGED_IDENTITY_PRINCIPAL_ID=$(az identity show --name $MANAGED_IDENTITY_NAME --resource-group $RESOURCE_GROUP --query principalId -o tsv)

# Introduce a delay to allow AAD to propagate the service principal
echo "Waiting for the managed identity's service principal to be available..."
sleep 30  # Wait for 30 seconds

# Assign the Reader role to the managed identity at the subscription level
echo "Assigning Reader role to the managed identity..."
SUBSCRIPTION_ID=$(az account show --query id -o tsv)

# Retry loop for role assignment
for i in {1..5}; do
    az role assignment create --assignee $MANAGED_IDENTITY_PRINCIPAL_ID --role "Reader" --scope /subscriptions/$SUBSCRIPTION_ID && break
    echo "Role assignment failed, retrying in 10 seconds..."
    sleep 10
done

# Create a Function App (without assigning the identity initially)
echo "Creating Function App..."
az functionapp create \
  --name $FUNCTION_APP_NAME \
  --storage-account $STORAGE_ACCOUNT_NAME \
  --resource-group $RESOURCE_GROUP \
  --consumption-plan-location $LOCATION \
  --functions-version 4 \
  --os-type Windows \
  --runtime dotnet \
  --runtime-version 8  # Updated to use .NET 8

# Assign the user-assigned managed identity to the Function App using the full resource ID
echo "Assigning the managed identity to the Function App..."
az webapp identity assign --resource-group $RESOURCE_GROUP --name $FUNCTION_APP_NAME --identities $MANAGED_IDENTITY_RESOURCE_ID

# Configure the Function App settings
echo "Configuring Function App settings..."
az functionapp config appsettings set --name $FUNCTION_APP_NAME --resource-group $RESOURCE_GROUP --settings "MANAGED_IDENTITY_CLIENT_ID=$MANAGED_IDENTITY_CLIENT_ID"

# Configure CORS to allow all origins
echo "Configuring CORS to allow all origins..."
az functionapp cors add --name $FUNCTION_APP_NAME --resource-group $RESOURCE_GROUP --allowed-origins '*'

# Download and deploy the Function App package
echo "Deploying Function App..."
curl -L $ZIP_URL -o functionapp.zip
az functionapp deployment source config-zip \
  --name $FUNCTION_APP_NAME \
  --resource-group $RESOURCE_GROUP \
  --src functionapp.zip

# Output the Function App URL
FUNCTION_APP_URL=$(az functionapp show --name $FUNCTION_APP_NAME --resource-group $RESOURCE_GROUP --query "defaultHostName" -o tsv)
echo "Deployment completed. Function App URL: https://$FUNCTION_APP_URL"
