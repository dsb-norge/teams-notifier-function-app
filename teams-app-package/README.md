# Teams App Package

This directory contains the manifest generation tooling for the Teams Notification Bot.

## Files

- `create-teams-app-package.sh` - Generates `manifest.json` and ZIP package from the app contract and branding metadata
- `Upload-TeamsAppPackage.ps1` - Uploads/updates the ZIP package to the org app catalog via Microsoft Graph SDK
- `app-metadata.json` - App names, descriptions, developer info (customize per deployment)
- `color.png` - 192x192 color icon (generic placeholder)
- `outline.png` - 32x32 outline icon (generic placeholder)
- `manifest.json` - Generated Teams App manifest (output of the script -- do not edit directly)

## Creating the ZIP Package

The script reads from two sources plus a required argument:

1. **App contract** (`src/TeamsNotificationBot/app-contract.json`) -- commands, scopes, version (generated from code)
2. **Branding metadata** (`app-metadata.json`) -- app names, descriptions, developer info
3. **Bot App ID** (argument) -- the Entra ID app registration client ID for the bot

```bash
cd teams-app-package

# Basic usage (uses default icons from this directory)
./create-teams-app-package.sh --bot-app-id <GUID>

# With custom branded icons
./create-teams-app-package.sh --bot-app-id <GUID> --icons-dir ../deployment/branding/

# Preview manifest without creating ZIP
./create-teams-app-package.sh --bot-app-id <GUID> --dry-run
```

### All options

| Flag | Required | Default | Description |
|------|----------|---------|-------------|
| `--bot-app-id <GUID>` | Yes | -- | Entra ID app registration client ID |
| `--contract <path>` | No | `../src/TeamsNotificationBot/app-contract.json` | Path to app contract |
| `--metadata <path>` | No | `./app-metadata.json` | Path to branding metadata |
| `--icons-dir <dir>` | No | Script directory | Directory containing `color.png` and `outline.png` |
| `--output-dir <dir>` | No | Script directory | Output directory for manifest and ZIP |
| `--dry-run` | No | -- | Generate manifest only, skip ZIP |

## Customizing Branding

Edit `app-metadata.json` to set your organization's names and URLs:

```json
{
  "short_name": "Your Bot Name",
  "full_name": "Your Organization Notification Bot",
  "developer_name": "Your Organization",
  "developer_website": "https://your-org.example.com",
  "privacy_url": "https://your-org.example.com/privacy",
  "terms_url": "https://your-org.example.com/terms",
  "accent_color": "#003366",
  "output_zip": "your-bot-name.zip"
}
```

## Icons

The Teams manifest requires two PNG icons. The defaults in this directory are generic
placeholders. Replace them with your branded versions for production use, either by
replacing the files in place or using `--icons-dir` to point at a separate directory.

### Color icon (`color.png`)

| Requirement | Value |
|-------------|-------|
| **Format** | PNG |
| **Size** | Exactly 192x192 pixels |
| **Shape** | Full square (no rounded corners -- Teams applies masking automatically) |
| **Background** | Full bleed with your brand color; utilize the entire 192x192 area |
| **Logo safe area** | Keep your logo/symbol within the center 120x120 pixels |
| **Border** | Do not add a border (Teams adds one dynamically) |
| **Contrast** | Minimum 4.5:1 ratio between icon and background for accessibility |
| **Max file size** | No official limit, but keep under 50 KB for fast loading |

The color icon appears in the Teams app store, app flyouts, and the Manage Apps page.

**Do:**
- Use a flat brand color as background
- Keep the logo centered in the 120x120 safe area
- Abbreviate long names to stay readable at small sizes

**Don't:**
- Round the corners (Teams does this)
- Add a border (Teams does this)
- Place the logo in a circle inside the square
- Use low-contrast or faded artwork

### Outline icon (`outline.png`)

| Requirement | Value |
|-------------|-------|
| **Format** | PNG with transparency |
| **Size** | Exactly 32x32 pixels |
| **Colors** | White (`#FFFFFF`) only on a transparent background |
| **Content** | A simplified outline version of your logo |

The outline icon appears in the Teams app bar (left rail) and in messaging extension
compose areas. It is rendered on varying background colors, so only white + transparency
is allowed.

**Do:**
- Use only white pixels on transparent background
- Keep the design simple and recognizable at 32x32

**Don't:**
- Use any color other than white
- Use a solid/opaque background

### References

- [Design App Icon for Teams Store](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/design/design-teams-app-icon-store-appbar)
- [Package your app](https://learn.microsoft.com/en-us/microsoftteams/platform/concepts/build-and-test/apps-package)

## Uploading to Teams

**Option 1: PowerShell (via Microsoft Graph SDK)**

Requires `Microsoft.Graph.Teams` and `Microsoft.Graph.Authentication` modules.
The user must hold the **Teams Administrator** Entra role (or higher) to consent to `AppCatalog.ReadWrite.All`.

```powershell
# Install modules (one time)
Install-Module Microsoft.Graph.Teams -Scope CurrentUser
Install-Module Microsoft.Graph.Authentication -Scope CurrentUser

# Upload new app (publish directly -- opens browser for auth)
./Upload-TeamsAppPackage.ps1 -Action Upload -PackagePath teams-notification-bot.zip

# Upload for admin review (lower permission: AppCatalog.Submit)
./Upload-TeamsAppPackage.ps1 -Action Upload -PackagePath teams-notification-bot.zip -RequiresReview

# List existing org apps (to find app ID for updates)
./Upload-TeamsAppPackage.ps1 -Action List

# Update an existing app
./Upload-TeamsAppPackage.ps1 -Action Update -PackagePath teams-notification-bot.zip -AppId "<app-id>"

# Dry run (no auth, no API calls)
./Upload-TeamsAppPackage.ps1 -Action Upload -PackagePath teams-notification-bot.zip -DryRun
```

> **Note:** Azure CLI (`az account get-access-token`) cannot be used for this -- its first-party app
> registration does not have `AppCatalog.*` scopes pre-authorized on Microsoft Graph. The Graph
> PowerShell SDK uses dynamic consent and does not have this limitation.

**Option 2: Manual**

1. Go to Teams Admin Center > Teams apps > Manage apps
2. Click "Upload new app"
3. Select your ZIP package

Or: In Teams client > Apps > Manage your apps > Upload an app to your org's app catalog

## Notes

- The manifest uses `isNotificationOnly: false` with `scopes: ["team", "personal", "groupChat"]`
- RSC permission `ChannelMessage.Send.Group` enables proactive messaging to channels
- The `commandLists` field is included only when present in the contract (supports both notification-only and interactive bots)
