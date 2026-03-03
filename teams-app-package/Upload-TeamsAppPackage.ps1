#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Uploads a Teams App ZIP package to the organization's app catalog via Microsoft Graph.

.DESCRIPTION
    Uses the Microsoft Graph PowerShell SDK to publish, update, or list Teams apps
    in the organization's app catalog. Unlike Azure CLI, the Graph PowerShell SDK
    supports dynamic consent and can request AppCatalog scopes.

    Requires the Microsoft.Graph.Teams module. Install with:
        Install-Module Microsoft.Graph.Teams -Scope CurrentUser

    Authentication uses interactive browser login. The user must have (or an admin
    must consent to) the appropriate Graph permissions:
        - AppCatalog.ReadWrite.All  → publish/update directly
        - AppCatalog.Submit         → submit for admin review

    The user must also hold the 'Teams Administrator' Entra role (or higher)
    when consenting to AppCatalog.ReadWrite.All.

.PARAMETER Action
    The operation to perform: Upload, Update, or List.
    - Upload: Publish a new app to the org catalog
    - Update: Update an existing app with a new package
    - List:   List all custom apps in the org catalog

.PARAMETER PackagePath
    Path to the Teams App ZIP package. Required for Upload and Update actions.

.PARAMETER AppId
    The Graph teamsApp ID of the existing app to update. Required for Update action.
    Use -Action List to find this ID.

.PARAMETER RequiresReview
    When uploading, submit the app for admin review instead of publishing directly.
    Uses AppCatalog.Submit permission (user-consentable) instead of AppCatalog.ReadWrite.All.

.PARAMETER TenantId
    Optional tenant ID. If not specified, uses the default tenant for the signed-in user.

.PARAMETER DryRun
    Show what would be done without making API calls or authenticating.

.EXAMPLE
    # Upload new app (publish directly)
    ./Upload-TeamsAppPackage.ps1 -Action Upload -PackagePath teams-notification-bot.zip

.EXAMPLE
    # Upload new app for admin review
    ./Upload-TeamsAppPackage.ps1 -Action Upload -PackagePath teams-notification-bot.zip -RequiresReview

.EXAMPLE
    # List existing custom apps
    ./Upload-TeamsAppPackage.ps1 -Action List

.EXAMPLE
    # Update an existing app
    ./Upload-TeamsAppPackage.ps1 -Action Update -PackagePath teams-notification-bot.zip -AppId "e3e29acb-..."
#>

[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet("Upload", "Update", "List")]
    [string]$Action,

    [Parameter()]
    [string]$PackagePath,

    [Parameter()]
    [string]$AppId,

    [Parameter()]
    [switch]$RequiresReview,

    [Parameter()]
    [string]$TenantId,

    [Parameter()]
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# --- Helper functions ---

function Write-Step {
    param([string]$Message)
    Write-Host "  $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "  ✅ $Message" -ForegroundColor Green
}

function Write-Err {
    param([string]$Message)
    Write-Host "  ❌ $Message" -ForegroundColor Red
}

function Write-Warn {
    param([string]$Message)
    Write-Host "  ⚠️  $Message" -ForegroundColor Yellow
}

function Get-GraphErrorDetail {
    param($ErrorRecord)

    $detail = @{
        Message    = $ErrorRecord.Exception.Message
        StatusCode = $null
        Body       = $null
    }

    # Try to extract the HTTP response body from the exception
    $innerEx = $ErrorRecord.Exception
    while ($innerEx) {
        # Microsoft.Graph.Models.ODataErrors.ODataError
        if ($innerEx.PSObject.Properties['ResponseStatusCode']) {
            $detail.StatusCode = $innerEx.ResponseStatusCode
        }
        if ($innerEx.PSObject.Properties['Error'] -and $innerEx.Error) {
            $detail.Body = $innerEx.Error | ConvertTo-Json -Depth 5 -Compress
            break
        }
        # HttpRequestException or similar with Response
        if ($innerEx.PSObject.Properties['Response'] -and $innerEx.Response) {
            $detail.StatusCode = [int]$innerEx.Response.StatusCode
            try {
                $stream = $innerEx.Response.Content.ReadAsStringAsync().Result
                if ($stream) { $detail.Body = $stream }
            }
            catch { }
            break
        }
        $innerEx = $innerEx.InnerException
    }

    # Fallback: check ErrorDetails on the ErrorRecord itself
    if (-not $detail.Body -and $ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
        $detail.Body = $ErrorRecord.ErrorDetails.Message
    }

    return $detail
}

function Write-GraphError {
    param($ErrorRecord, [string]$Context = "Operation")

    $detail = Get-GraphErrorDetail $ErrorRecord

    Write-Host "  Error:       $($detail.Message)" -ForegroundColor Red
    if ($detail.StatusCode) {
        Write-Host "  Status Code: $($detail.StatusCode)" -ForegroundColor Red
    }
    if ($detail.Body) {
        Write-Host "  API Response:" -ForegroundColor Yellow
        # Try to pretty-print JSON, fall back to raw
        try {
            $parsed = $detail.Body | ConvertFrom-Json
            $pretty = $parsed | ConvertTo-Json -Depth 5
            $pretty -split "`n" | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
        }
        catch {
            $detail.Body -split "`n" | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
        }
    }

    return $detail
}

function Assert-ModuleInstalled {
    param([string]$ModuleName)

    if (-not (Get-Module -ListAvailable -Name $ModuleName)) {
        Write-Err "Required module '$ModuleName' is not installed."
        Write-Host ""
        Write-Host "  Install it with:" -ForegroundColor Yellow
        Write-Host "    Install-Module $ModuleName -Scope CurrentUser" -ForegroundColor White
        Write-Host ""
        throw "Module '$ModuleName' not found."
    }
}

function Connect-GraphWithScopes {
    param(
        [string[]]$Scopes,
        [string]$TenantId
    )

    # Check if already connected with the required scopes
    $context = Get-MgContext -ErrorAction SilentlyContinue
    if ($context) {
        $missingScopes = $Scopes | Where-Object { $_ -notin $context.Scopes }
        if (-not $missingScopes) {
            Write-Step "Already connected to Microsoft Graph as $($context.Account)"
            return
        }
        Write-Step "Connected but missing scopes: $($missingScopes -join ', '). Reconnecting..."
    }

    Write-Step "Connecting to Microsoft Graph..."
    Write-Step "Scopes requested: $($Scopes -join ', ')"
    Write-Host ""
    Write-Warn "A browser window will open for authentication."
    Write-Warn "You must sign in with an account that has Teams Administrator role"
    Write-Warn "(or an admin must consent) for AppCatalog permissions."
    Write-Host ""

    $connectParams = @{
        Scopes    = $Scopes
        NoWelcome = $true
    }
    if ($TenantId) {
        $connectParams.TenantId = $TenantId
    }

    try {
        Connect-MgGraph @connectParams
    }
    catch {
        Write-Err "Failed to connect to Microsoft Graph."
        Write-Host ""
        Write-Host "  Common causes:" -ForegroundColor Yellow
        Write-Host "    - User cancelled the authentication" -ForegroundColor White
        Write-Host "    - Admin consent required but not granted" -ForegroundColor White
        Write-Host "    - Network/proxy blocking the auth flow" -ForegroundColor White
        Write-Host ""
        throw
    }

    $context = Get-MgContext
    if (-not $context) {
        throw "Failed to establish Microsoft Graph connection."
    }

    Write-Success "Connected as $($context.Account) (Tenant: $($context.TenantId))"

    # Verify scopes were granted
    $missingScopes = $Scopes | Where-Object { $_ -notin $context.Scopes }
    if ($missingScopes) {
        Write-Warn "The following scopes were not granted: $($missingScopes -join ', ')"
        Write-Warn "The operation may fail. Admin consent may be required."
    }
}

function Invoke-ListApps {
    Write-Host ""
    Write-Host "Listing custom apps in organization catalog..." -ForegroundColor Cyan
    Write-Host ""

    if ($DryRun) {
        Write-Host "[DRY RUN] Would call: Get-MgAppCatalogTeamApp (all custom apps, admin view)" -ForegroundColor Yellow
        return
    }

    # NOTE: Known Graph API limitation — apps with restricted availability
    # (configured in Teams Admin Center) are NOT returned by the
    # /appCatalogs/teamsApps endpoint, even with AppCatalog.ReadWrite.All.
    # The Teams Admin Center uses internal APIs not available via Graph.
    # For a complete list including restricted apps, use the MicrosoftTeams
    # PowerShell module: Get-AllM365TeamsApps (requires Connect-MicrosoftTeams).
    Connect-GraphWithScopes -Scopes @("AppCatalog.ReadWrite.All") -TenantId $TenantId

    try {
        $apps = Get-MgAppCatalogTeamApp `
            -ExpandProperty "appDefinitions" `
            -All

        # Exclude Microsoft store apps (keep organization, sideloaded, etc.)
        $apps = $apps | Where-Object { $_.DistributionMethod -ne "store" }
    }
    catch {
        Write-Err "Failed to list apps from catalog."
        Write-Host "  Error: $($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Message -match "403|Forbidden|Authorization") {
            Write-Host ""
            Write-Warn "Permission denied. Your account may need AppCatalog.ReadWrite.All permission."
        }
        throw
    }

    if (-not $apps -or $apps.Count -eq 0) {
        Write-Host "  No custom apps found in organization catalog." -ForegroundColor Yellow
        return
    }

    Write-Host "  Found $($apps.Count) custom app(s):" -ForegroundColor Green
    Write-Host ""

    foreach ($app in $apps) {
        Write-Host "  ID:            $($app.Id)" -ForegroundColor White
        Write-Host "  External ID:   $($app.ExternalId)" -ForegroundColor Gray
        Write-Host "  Display Name:  $($app.DisplayName)" -ForegroundColor White
        Write-Host "  Distribution:  $($app.DistributionMethod)" -ForegroundColor Gray

        # Show publishing state and availability from app definitions
        if ($app.AppDefinitions -and $app.AppDefinitions.Count -gt 0) {
            $latestDef = $app.AppDefinitions | Select-Object -Last 1
            Write-Host "  Versions:      $($app.AppDefinitions.Count)" -ForegroundColor Gray

            $pubState = $null
            if ($latestDef.PSObject.Properties['PublishingState'] -and $latestDef.PublishingState) {
                $pubState = $latestDef.PublishingState
            }
            elseif ($latestDef.PSObject.Properties['publishingState'] -and $latestDef.publishingState) {
                $pubState = $latestDef.publishingState
            }
            if ($pubState) {
                $pubColor = switch ($pubState) {
                    "published" { "Green" }
                    "submitted" { "Yellow" }
                    "rejected"  { "Red" }
                    default     { "Gray" }
                }
                Write-Host "  Pub. State:    $pubState" -ForegroundColor $pubColor
            }

            # Show allowed/blocked status from authorization (available in some Graph versions)
            $allowedInstall = $null
            if ($latestDef.PSObject.Properties['Authorization'] -and $latestDef.Authorization) {
                $allowedInstall = $latestDef.Authorization
            }
            if ($latestDef.PSObject.Properties['AllowedInstallationScopes'] -and $latestDef.AllowedInstallationScopes) {
                Write-Host "  Install Scope: $($latestDef.AllowedInstallationScopes)" -ForegroundColor Gray
            }
        }
        else {
            Write-Host "  Versions:      0" -ForegroundColor Yellow
        }
        Write-Host "  ---" -ForegroundColor DarkGray
    }

    Write-Host ""
    Write-Host "  Use the 'ID' value with -Action Update -AppId <ID> to update an existing app." -ForegroundColor Cyan
    Write-Host ""
    Write-Warn "Apps with restricted availability in Teams Admin Center are NOT visible via Graph API."
    Write-Host "  For a complete list, use: " -NoNewline -ForegroundColor Yellow
    Write-Host "Get-AllM365TeamsApps" -ForegroundColor White
    Write-Host "  (requires MicrosoftTeams PowerShell module + Connect-MicrosoftTeams)" -ForegroundColor DarkGray
}

function Invoke-UploadApp {
    param([string]$ZipPath, [bool]$ForReview)

    Write-Host ""
    Write-Host "Uploading Teams App package..." -ForegroundColor Cyan
    Write-Host "  Package:  $ZipPath"
    Write-Host "  Size:     $((Get-Item $ZipPath).Length) bytes"

    if ($ForReview) {
        Write-Host "  Mode:     Submit for admin review" -ForegroundColor Yellow
    }
    else {
        Write-Host "  Mode:     Publish directly" -ForegroundColor Green
    }

    if ($DryRun) {
        Write-Host ""
        Write-Host "[DRY RUN] Would upload $ZipPath to organization app catalog" -ForegroundColor Yellow
        return
    }

    $scope = if ($ForReview) { "AppCatalog.Submit" } else { "AppCatalog.ReadWrite.All" }
    Connect-GraphWithScopes -Scopes @($scope) -TenantId $TenantId

    Write-Host ""
    Write-Step "Reading ZIP package..."
    try {
        [byte[]]$zipContent = [System.IO.File]::ReadAllBytes((Resolve-Path $ZipPath).Path)
    }
    catch {
        Write-Err "Failed to read ZIP file: $($_.Exception.Message)"
        throw
    }
    Write-Step "Read $($zipContent.Length) bytes"

    Write-Step "Uploading to organization app catalog..."
    try {
        $uri = "https://graph.microsoft.com/v1.0/appCatalogs/teamsApps"
        if ($ForReview) {
            $uri += "?requiresReview=true"
        }
        $result = Invoke-MgGraphRequest -Method POST -Uri $uri `
            -Body $zipContent -ContentType "application/zip" `
            -OutputType PSObject
    }
    catch {
        Write-Err "Upload failed."
        Write-Host ""

        $detail = Write-GraphError $_ -Context "Upload"
        $errorMessage = $detail.Message

        Write-Host ""
        if ($errorMessage -match "403|Forbidden") {
            Write-Host "  Ensure your account has:" -ForegroundColor Yellow
            if ($ForReview) {
                Write-Host "    - AppCatalog.Submit (delegated)" -ForegroundColor White
            }
            else {
                Write-Host "    - AppCatalog.ReadWrite.All (delegated, requires admin consent)" -ForegroundColor White
                Write-Host ""
                Write-Host "  Try with -RequiresReview to submit for review instead (lower permission)." -ForegroundColor Cyan
            }
        }
        elseif ($errorMessage -match "409|Conflict") {
            Write-Host "  An app with the same external ID already exists." -ForegroundColor Red
            Write-Host "  Use -Action List to find the existing app ID," -ForegroundColor Yellow
            Write-Host "  then -Action Update -AppId <id> to update it." -ForegroundColor Yellow
        }
        throw
    }

    Write-Host ""
    Write-Success "App published successfully!"
    Write-Host ""

    $appId = $null
    if ($result -is [hashtable] -or $result.PSObject) {
        $appId = if ($result.id) { $result.id } elseif ($result.Id) { $result.Id } else { "unknown" }
        $displayName = if ($result.displayName) { $result.displayName } elseif ($result.DisplayName) { $result.DisplayName } else { "unknown" }
        $externalId = if ($result.externalId) { $result.externalId } elseif ($result.ExternalId) { $result.ExternalId } else { "unknown" }

        Write-Host "  Teams App ID:   $appId" -ForegroundColor White
        Write-Host "  External ID:    $externalId" -ForegroundColor Gray
        Write-Host "  Display Name:   $displayName" -ForegroundColor White
    }

    Write-Host ""
    Write-Host "  Next steps:" -ForegroundColor Cyan
    if ($ForReview) {
        Write-Host "    1. An admin must approve the app in Teams Admin Center" -ForegroundColor White
        Write-Host "       https://admin.teams.microsoft.com/policies/manage-apps" -ForegroundColor Gray
    }
    Write-Host "    - Install the app in target teams" -ForegroundColor White
    if ($appId -and $appId -ne "unknown") {
        Write-Host "    - To update later: -Action Update -AppId $appId" -ForegroundColor White
    }
}

function Invoke-UpdateApp {
    param([string]$ZipPath, [string]$TargetAppId)

    Write-Host ""
    Write-Host "Updating existing Teams App..." -ForegroundColor Cyan
    Write-Host "  Package:  $ZipPath"
    Write-Host "  Size:     $((Get-Item $ZipPath).Length) bytes"
    Write-Host "  App ID:   $TargetAppId"

    if ($DryRun) {
        Write-Host ""
        Write-Host "[DRY RUN] Would update app $TargetAppId with $ZipPath" -ForegroundColor Yellow
        return
    }

    Connect-GraphWithScopes -Scopes @("AppCatalog.ReadWrite.All") -TenantId $TenantId

    # Verify the app exists first
    Write-Step "Verifying app exists..."
    try {
        $existingApp = Get-MgAppCatalogTeamApp -TeamsAppId $TargetAppId -ErrorAction Stop
        Write-Step "Found app: $($existingApp.DisplayName)"
    }
    catch {
        Write-Err "App with ID '$TargetAppId' not found in the organization catalog."
        Write-Host ""
        Write-Host "  Use -Action List to see available apps and their IDs." -ForegroundColor Yellow
        throw
    }

    Write-Step "Reading ZIP package..."
    try {
        [byte[]]$zipContent = [System.IO.File]::ReadAllBytes((Resolve-Path $ZipPath).Path)
    }
    catch {
        Write-Err "Failed to read ZIP file: $($_.Exception.Message)"
        throw
    }
    Write-Step "Read $($zipContent.Length) bytes"

    Write-Step "Uploading new version..."
    try {
        $uri = "https://graph.microsoft.com/v1.0/appCatalogs/teamsApps/$TargetAppId/appDefinitions"
        $result = Invoke-MgGraphRequest -Method POST -Uri $uri `
            -Body $zipContent -ContentType "application/zip" `
            -OutputType PSObject
    }
    catch {
        Write-Err "Update failed."
        Write-Host ""
        Write-GraphError $_ -Context "Update" | Out-Null
        throw
    }

    Write-Host ""
    Write-Success "App updated successfully!"
    if ($result) {
        $version = if ($result.version) { $result.version } elseif ($result.Version) { $result.Version } else { "unknown" }
        Write-Host "  Version: $version" -ForegroundColor White
    }
}

# --- Main ---

Write-Host ""
Write-Host "╔═══════════════════════════════════════════╗" -ForegroundColor DarkCyan
Write-Host "║  Teams App Package Manager (Graph API)    ║" -ForegroundColor DarkCyan
Write-Host "╚═══════════════════════════════════════════╝" -ForegroundColor DarkCyan
Write-Host ""

# Validate parameters
if ($Action -in @("Upload", "Update")) {
    if (-not $PackagePath) {
        Write-Err "-PackagePath is required for $Action action."
        exit 1
    }

    $resolvedPath = $null
    try {
        $resolvedPath = (Resolve-Path $PackagePath -ErrorAction Stop).Path
    }
    catch {
        Write-Err "ZIP file not found: $PackagePath"
        exit 1
    }

    if (-not $resolvedPath.EndsWith(".zip")) {
        Write-Warn "File does not have .zip extension: $resolvedPath"
    }

    $PackagePath = $resolvedPath
}

if ($Action -eq "Update" -and -not $AppId) {
    Write-Err "-AppId is required for Update action."
    Write-Host "  Use -Action List to find the app ID." -ForegroundColor Yellow
    exit 1
}

# Check module availability (skip for dry run)
if (-not $DryRun) {
    try {
        Assert-ModuleInstalled "Microsoft.Graph.Teams"
        Assert-ModuleInstalled "Microsoft.Graph.Authentication"
    }
    catch {
        exit 1
    }

    Import-Module Microsoft.Graph.Teams -ErrorAction Stop
}

# Execute action
try {
    switch ($Action) {
        "List" {
            Invoke-ListApps
        }
        "Upload" {
            Invoke-UploadApp -ZipPath $PackagePath -ForReview $RequiresReview.IsPresent
        }
        "Update" {
            Invoke-UpdateApp -ZipPath $PackagePath -TargetAppId $AppId
        }
    }
}
catch {
    Write-Host ""
    Write-Err "Operation failed: $($_.Exception.Message)"
    Write-Host ""
    exit 1
}

Write-Host ""
