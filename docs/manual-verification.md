# Manual verification checklist

Some behavior in this app cannot be pinned by automated tests and must be verified against real
Teams on the **dev** environment:

- **Proactive sends** — `BotService.SendMessageAsync` / `SendAdaptiveCardAsync` compose the concrete
  `CloudAdapter.ContinueConversationAsync` with Bot Framework token acquisition; no test executes
  that chain.
- **Channel enumeration** — `BotService.EnumerateAndStoreTeamChannelsAsync` runs inside a proactive
  turn and calls the Teams channel-list REST API with a self-built authenticated client.
- **Real Teams payloads** — install/uninstall, channel and team lifecycle events, and card invokes
  arrive with payload shapes that fixtures only approximate.

Run this checklist on dev before promoting any change that touches the bot turn pipeline, the
Agents SDK packages, `BotService`'s proactive paths, or authentication — and after the
`Microsoft.Agents.Extensions.MSTeams` migration specifically, run **all** of it.

## Setup

1. Deploy the build to dev (ops repo: `dsb-infra/azure-terraform-ikt-operations`, "Deploy Teams
   Notifier — dev").
2. **Enable the debug logging profile** (app settings on the Function App, added via the ops/infra
   repo or portal for a temporary session):

   | App setting | Value | Effect |
   |---|---|---|
   | `Logging__LogLevel__TeamsNotificationBot` | `Debug` | All handler/service debug logs (activity routing, channel-name resolution fallbacks, teamlookup resolution) |
   | `Logging__LogLevel__Microsoft.Agents` | `Debug` | Agents SDK internals — per-turn route-list matching from `AgentApplication`, adapter and auth diagnostics |

   For a migration-class rollout, deploy dev with these **on from the start** so the first
   exercised paths are captured; remove them once verification passes (Debug volume is noisy and
   App Insights sampling is capped at 20 items/s in `host.json`, so leaving them on degrades
   sampled telemetry).
3. Have App Insights open: `traces` for the debug logs, `dependencies` for storage/Bot Framework
   calls, `exceptions` for anything escaping to the `BotMessages` 500 envelope.

## Checklist

### 1. Install / uninstall

- [ ] **Install into a team** (pick a non-General channel during install):
  welcome message arrives in the chosen channel; `list-aliases` from that team works;
  App Insights shows the `enumerate_channels` bot operation being processed;
  a `set-alias` + notify (below) proves the install-channel reference row is usable.
- [ ] **Channel enumeration**: after install, aliases created for *other* channels in the team
  resolve names in `list-aliases` (rows were created with names by enumeration).
- [ ] **Install personal chat**: greeting with your name; bot answers `checkin`.
- [ ] **Install group chat**: greeting; `checkin` works.
- [ ] **Uninstall from team**: `remove_team_refs` operation processed; re-run `list-aliases`
  from another scope — aliases pointing at that team show raw IDs (references gone).
- [ ] **Uninstall personal + group chat**: no errors in App Insights.

### 2. Channel lifecycle (in a team where the bot is installed)

- [ ] Create a channel → alias it (`set-alias`) → notify works; `list-aliases` shows its name.
- [ ] Rename that channel → `list-aliases` shows the new name.
- [ ] Delete the channel → its reference row is removed (alias renders as raw IDs).
- [ ] Restore the channel → reference reappears; notify works again.

### 3. Team lifecycle

- [ ] Rename the team → `rename_team` operation processed; `list-aliases` shows the new team name.
- [ ] (Optional, destructive) Delete a scratch team → `remove_team_refs` processed.

### 4. Commands and cards

- [ ] `@Bot checkin` **as an @mention in a channel** (mention stripping) and `checkin` in
  personal chat; also `/checkin` with the slash prefix.
- [ ] `help`, `help webhooks`, unknown command.
- [ ] `set-alias` / `list-aliases` / `remove-alias` in channel, personal, and group chat.
- [ ] `create-alias` → submit the card (Action.Submit path) → alias created, confirmation posted.
- [ ] `delete-post`: reply to a bot message; quote a bot message; try it on a human message
  (should refuse politely).
- [ ] `queue-status` / `queue-peek` on an empty poison queue.

### 5. Proactive delivery (the untestable core)

- [ ] `POST /api/v1/notify/{alias}` (text) → message lands in the aliased channel as a **new
  top-level post**, not a thread reply — including after having chatted in a thread with the bot
  (verifies the `;messageid=` strip survives end-to-end).
- [ ] `POST /api/v1/alert/{alias}` → adaptive card renders.
- [ ] Notify a personal-chat alias and a group-chat alias.
- [ ] Webhook smoke: `create-webhook`, POST a sample updown payload to the ingest URL, card lands.
- [ ] (Optional) Force a poison message → poison alert card arrives at the `PoisonAlertAlias`.

### 6. Teardown

- [ ] Remove the two debug logging app settings.
- [ ] Remove scratch aliases/webhooks created above.

## Recording results

Note the date, build version (`checkin` prints it), and any deviations in the PR or release notes.
Storage rows on dev cannot be read directly with `az` (shared keys disabled; RBAC scoped) — verify
via bot commands and App Insights `dependencies.data` instead.
