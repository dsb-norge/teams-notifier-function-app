# Design: threads, delivery status, mentions and direct messages

| Field | Value |
|---|---|
| Status | Approved; releases 1, 2 and 3 implemented and verified on dev (§9) |
| Audience | app developers (this repo) and API consumers |
| First client | the notifications in [`dsb-norge/github-actions-terraform`](https://github.com/dsb-norge/github-actions-terraform) (unapplied default branch, nightly drift) |

This document records the design agreed with the first client. Every feature here is generic:
nothing in the API knows about GitHub, workflows, incidents or escalation. When a feature
lands, its behaviour moves into the permanent docs ([api-reference.md](../api-reference.md),
[architecture.md](../architecture.md)), which are the source of truth from then on.

---

## 1. Goal

Let a caller run one Teams thread per incident: open it with a post, add to it with replies,
and mark it resolved by updating the first post. Let the caller mention people and tags
safely, and reach a person directly. The bot stays a relay.

## 2. Principles

- **The bot relays.** No reminders, digests, deduplication or "is this worth sending" in the
  bot. That lives with the caller.
- **The bot knows nothing about the caller's domain.** Callers hand the bot Teams and Entra
  identities only.
- **Holding `Notifications.Send` is the whole permission** to post to any alias on an instance,
  and to read or change any message on it. There is no per-alias authorization.
- **Alias management from Teams stays open by design.** Anyone who can talk to the bot can
  create, repoint or remove any alias. Tracking an owner per alias, and handling owners who
  leave, costs more than it protects for now.
- **All API changes are additive.** Existing callers keep working, and no change to the
  Terraform module is needed: no new queues, required app settings, well-known routes or
  EasyAuth exclusions. New tables are created by the app.

## 3. Decisions

| # | Decision |
|---|---|
| D1 | Every message on the `notifications` queue gets a **delivery record**, whichever route queued it. |
| D2 | `GET /v1/messages/{messageId}` returns `status` (`queued`, `delivered`, `failed`), `postedAs` (`post`, `reply`, `update`), `target`, `unresolvedMentions`, `enqueuedAt`, `deliveredAt` and `error`. |
| D3 | `replyTo` and `update` on `POST /v1/notify/{alias}`, mutually exclusive. A reply waits for a parent that is still queued (§5.3). |
| D4 | `replyTo` is lenient and `update` is strict (§5.3). A parent sent to a different alias is `409` for both. |
| D5 | A thread stays where its parent went, even if the alias is repointed later. |
| D6 | Delivery records are kept 180 days by default; idempotency keys 7 days. Both are optional settings. |
| D7 | Permanent failures fail at once, without retries or a poison-queue alert. Transient failures retry as before. |
| D8 | Idempotency is scoped per caller, route and target, claimed atomically before queuing, expires, and is supported on `/v1/send` too. |
| D9 | Posting to an alias whose conversation the bot no longer has returns `404`, not `202` followed by the poison queue. |
| D10 | Every queued, delivered and failed message is logged as a custom event that is never sampled, with the calling principal and `metadata`. `metadata` is limited in size. |
| D11 | Mentions are placed with `<at>key</at>` and declared in a `mentions` array. Each person is checked against the roster before posting; the roster's display name is shown (§6). |
| D12 | Tag mentions use the same array. Tag IDs come with each request; the bot stores none. |
| D13 | Direct messages go through `/v1/send` with a personal target that accepts an object ID or a UPN, found through the roster of a team the bot is installed in (§7). |
| D14 | `Retry-After` on `429` is always a whole number of seconds, never an HTTP date. |

## 4. Releases

1. **Release 1**, in two pull requests:
   - **A:** idempotency rework (D8), `404` on a missing conversation (D9), unsampled delivery
     events and `metadata` limits (D10), `Retry-After` (D14), and the documentation corrections
     the first client found.
   - **B:** delivery records, the status endpoint, `replyTo` and `update` (D1–D7).
2. **Release 2:** mentions and tag mentions (D11, D12).
3. **Release 3:** direct messages (D13).

## 5. Release 1

### 5.1 Idempotency (A)

- **Scope.** A record is keyed by the route (`notify`, `send`) and the SHA-256 of the calling
  principal, the target (the alias, or the `/v1/send` target) and the caller's key. The same key
  from two callers, or for two aliases, never collides. Hashing also makes any printable key
  safe as a Table Storage row key, which rejects `/ \ # ?`.
- **Key rules.** 1–256 printable ASCII characters (`0x20`–`0x7E`), else `400`. An empty header
  counts as absent, as before.
- **Atomic claim.** The record is inserted as *pending* before the message is queued. A
  concurrent duplicate finds it pending and gets `409`. A pending record older than the function
  timeout (5 minutes) is abandoned and can be claimed again. When queuing fails, the claim is
  released so the caller can retry with the same key.
- **Replay.** A completed record replays the original status and body, including the original
  `messageId`. A different body under the same key is not detected.
- **Expiry.** A record older than `Idempotency__ExpiryHours` (default 168) reads as absent and
  can be claimed again. A daily timer deletes expired records, including those the updown
  ingress writes to deduplicate retries.

### 5.2 Missing conversation (A)

`/v1/notify`, `/v1/alert` and `/v1/checkin` check that the bot still has a conversation
reference for the alias's target. Without one they return `404` with a detail that tells the
two cases apart. The check is skipped when Teams integration is disabled (offline local mode),
which stores no references. A conversation lost between the request and delivery still fails
in the queue, as before.

### 5.3 Delivery events and `metadata` (A)

- **Events.** `NotificationQueued`, `NotificationDelivered` and `NotificationDeliveryFailed`
  are App Insights custom events. Each carries the message ID, source route, calling principal,
  alias or target, format and every `metadata` entry as a `meta.<key>` property. Delivery events
  add the dequeue count. Each event has its sampling percentage pinned to 100, which the
  App Insights SDK documents as the way to keep an item out of SDK sampling. Portal ingestion
  sampling, if someone turns it on, still applies.
- **Limits.** At most 10 `metadata` keys. Keys are 1–64 characters of letters, digits, `.`,
  `_` and `-`; values are strings of at most 256 characters. Otherwise `400`. `metadata` is
  logged, so it must not carry secrets.

### 5.4 Delivery records and the status endpoint (B)

- **Record.** One row per `messageId`: status, source route, calling principal, alias, target
  conversation, Teams activity ID, `postedAs`, error and timestamps. Written as `queued` when
  queued, `delivered` with the activity ID on success, and `failed` with the error on a
  permanent failure or when the poison-queue monitor sees the message. Retention
  `DeliveryRecords__RetentionDays` (default 180), purged by the same daily timer.
- **No double posts.** The queue processor skips a message whose record is already
  `delivered`, so a queue retry after a successful send no longer posts twice.
- **Status endpoint.** `GET /v1/messages/{messageId}` returns the record. Unknown or expired
  IDs return `404`. It counts against the per-principal rate limit like any other API call.

### 5.5 `replyTo` and `update` (B)

| Parent state | `replyTo` | `update` |
|---|---|---|
| Delivered | Reply in the parent's thread | Message replaced |
| Still queued | Held until the parent is delivered or failed, at most 10 minutes, then as below | Held, the same way |
| Failed | New top-level post | Fails, with no new post |
| Unknown or expired | New top-level post | `404` at request time |
| Sent to a different alias | `409` at request time | `409` at request time |

- **Holding.** A held message is put back on the queue with a visibility delay rather than
  failed, so waiting doesn't use up its retries or end in the poison queue.
- **Threads.** A reply goes to `<channel>;messageid=<parent activity>` in the parent's
  conversation (D5). Chats have no threads, so a reply there is a normal message with
  `postedAs: "post"`. `update` works in channels and chats.
- **Updates.** An update replaces the whole message; the format may change. It gets its own
  `messageId` for its status, but further replies and updates reference the original root.

## 6. Release 2: mentions and tags

```json
{
  "format": "text",
  "message": "Apply failed after merge. <at>merger</at>, please take a look.",
  "mentions": [
    { "key": "merger", "id": "jane.doe@example.com", "name": "Jane Doe" },
    { "key": "oncall", "tag": "<tag ID>", "name": "On call" }
  ]
}
```

- **Placement.** Every `<at>…</at>` must match a declared key, and every key must be used, else
  `400`. No orphaned `<at>` tag can reach Teams: an unmatched tag makes Teams shift the other
  mentions onto the wrong people. Matching runs on the raw text; HTML entities are not decoded
  first.
- **People.** `id` is an Entra object ID or a UPN. It is checked against the roster in the queue
  processor: the team's roster for a channel alias (a private channel's own members, which
  leaves out team members who aren't in it), the chat's members for a group chat.
  Mentions on a personal alias are `400`. A match is rendered as `<at>roster name</at>` with a
  mention entity; a miss as the caller's `name` in plain text, listed in `unresolvedMentions`.
  The roster's name is shown so the person displayed is always the person pinged. Lookups are
  cached in memory for a few minutes. At most 20 person mentions per message.
- **Tags.** `tag` is the tag ID, `name` is required, and `id` and `tag` are exclusive. Tags are
  not validated (that needs Graph). Channel aliases only, at most 10 per message, else `400`.
  Teams limits tag mentions to 2 messages per 5 seconds and 5 per minute per thread; the bot
  doesn't enforce that, and Teams throttling delays a burst rather than losing it.
- **Raw entities.** A card that uses `mentions` may not also carry mention entities in
  `msteams.entities` (`400`). A card without `mentions` is forwarded unchanged, as today, and
  its entities are not checked.
- **Updates and replies** take `mentions` too.

Settled while implementing:

- **`/v1/notify` only.** The agreement named `/v1/notify`; `/v1/send` can take `mentions` later
  if a client needs it.
- **The roster is read whole, not per person.** Microsoft documents paged roster reads for teams
  and channels, but not whether a single-member lookup accepts a UPN or object ID. The bot reads
  the roster of the conversation it posts to (paged for a channel, whole for a chat) and matches
  the object ID and `userPrincipalName`. Teams names the object ID `aadObjectId` in the paged
  roster and `objectId` in the whole one, so both are read. One read per conversation per
  5 minutes, however many people a message mentions.
- **`name` is optional for a person.** It is only the plain-text fallback; the `id` is shown
  without it. It stays required for a tag.
- **A 400 from Teams for a message with tags is retried once without them.** Microsoft documents
  a `400` for a tag ID the team doesn't have, so an unchecked tag would otherwise fail the message
  on every retry. The resend writes the tags as plain text and reports their IDs in
  `unresolvedMentions`, the same way a person missing from the roster is reported.
- **A roster Teams refuses (a 4xx other than 429) doesn't fail the message**: its people are
  written as plain text and reported. A 5xx or a network error still propagates for the queue to
  retry.
- **One mention entity per placement, in the order the placements appear** (a card's string
  values in document order), not one per declared key. Teams pairs `<at>` tags with entities by
  position, so a key placed twice or placed out of declaration order would otherwise ping the
  wrong person.
- **The target check is made at request time and not repeated.** If the alias is repointed while
  the message is queued, mentions its new conversation can't take are written as plain text and
  reported, like any other unresolved mention, rather than failing a message that was accepted.

## 7. Release 3: direct messages

```json
{
  "target": { "type": "personal", "userId": "jane.doe@example.com" },
  "format": "text",
  "message": "Apply failed in production."
}
```

- `userId` accepts an object ID or a UPN. A stored one-to-one conversation is used if one
  exists. Otherwise the bot searches the rosters of the teams it is installed in (first match
  wins, or only `target.teamId` when given), creates the conversation from the roster's
  `29:` user ID, and stores it for next time. No personal installation and no Graph permission
  are needed.
- A person found in no roster is a permanent failure (D7).
- `update` works for direct messages with the rules in §5.5. `replyTo` doesn't apply.
- One request is one delivery, so a failed direct message can't make a retry post the channel
  message twice.

Settled while implementing:

- **`update` comes to `/v1/send` for every target type**, not only people: the rules are the same
  and nothing about them is specific to direct messages.
- **"The same target" means the same request target**: the same type and IDs, `userId` without
  regard to case. The record keeps the target as requested (`RequestedTarget`) and an update must
  match it, so a person named by UPN must be named by that UPN again. Comparing the resolved person instead would need the lookup at
  request time, which happens only at delivery. `target.teamId` only narrows a person's search,
  so it isn't part of the comparison.
- **A UPN is resolved through team rosters only.** Stored personal conversations are keyed by
  object ID, and a personal installation doesn't tell the bot the person's UPN, so someone who
  shares no team with the bot is reachable by object ID only. Storing a UPN per conversation
  would take a roster read at install time; not worth it until a client needs it.
- **No record lacks `RequestedTarget`.** Delivery records and `RequestedTarget` ship in the same
  release, so no compatibility fallback is needed for older `/v1/send` records.
- **`userId` is validated** as an object ID or a UPN (`400` otherwise). Before, any string was
  accepted, and anything but an object ID with a stored conversation ended in the poison queue.
- **The team roster is read at the team's thread ID**, through a stored conversation of the
  team (its General channel's when stored), and cached with the mention rosters.
- **The created conversation is stored from the reference `CreateConversationAsync` returns**,
  without running a turn in it, under the same `user`/`<object ID>` key a personal installation
  writes.

## 8. Settings

All optional, with defaults in code, so none is added to `app-requirements.json`:

| Setting | Default | Release |
|---|---|---|
| `Idempotency__ExpiryHours` | `168` (7 days) | 1A |
| `DeliveryRecords__RetentionDays` | `180` | 1B |

## 9. To verify on dev

Verified on dev on 2026-10-08, on a pre-release of all three releases, through the API as a
caller holding `Notifications.Send`:

- **Replies and updates (B).** A reply lands in its parent's thread and an update replaces the
  post in place (Teams marks it Edited); their `postedAs` says `reply` and `update`. A reply sent while
  its parent was still queued was held and posted into the thread about 20 seconds later.
- **Mentions (2).** By UPN, by object ID, in text and in a card, and the same person twice: each
  is a real mention under the roster's display name. A person missing from the roster, placed
  before a real one, is plain text and doesn't shift the mention after it. A mail address that
  isn't the UPN matches nobody and is reported, as documented. Six messages with mentions,
  processed at the same time, read the channel's roster once.
- **An invalid tag ID (2).** The message lands with the tag as plain text, the person mentioned
  after it is still the one pinged, and the tag ID is reported in `unresolvedMentions`. The
  outbound calls show Teams answering the first send with `400`, as Microsoft documents, and the
  resend succeeding.
- **Escaping in text messages (2).** `\*` and `\_` render literally (no bold or italics);
  `&lt;b&gt;` and `\<b\>` both render as a literal `<b>`; a `<` or `>` that forms no tag renders
  as written.
- **Direct messages (3).** By UPN and by object ID, both reach the person's existing chat with the
  bot (the UPN through a read of the team's roster at its thread ID; no conversation was created);
  an update replaces the message; a stranger, and a person searched for in the wrong team,
  fail with their reasons; an update naming the person in the other form is `409`. A team
  member whose stored conversation had been removed was found in the team roster by UPN; the bot
  created the chat (Teams returned the one the person already had), stored it with the roster's
  name, and a second message by object ID used the stored chat. A personal installation stores
  a conversation even if the person never writes to the bot, so "never chatted" is not the same
  as "unknown to the bot".

A second round, the same day, on a pre-release with the fixes below:

- **A private channel (2).** A private channel the app was added to after the team install had
  no stored conversation, so an alias set there was `404` on every notify; fixed by storing a
  channel from the first message the bot gets in it. Then: a channel member is mentioned; a
  team member who isn't in the channel is written as plain text and reported, since the
  channel's roster holds only its own members; a tag is plain text and reported (Teams rejects
  it, and Microsoft documents tags as unsupported there); thread, reply and update work.
- **A group chat (2).** People mentioned by UPN are pinged, a non-member is reported, a tag is
  `400` at request time, `replyTo` posts an ordinary message, and an update replaces the
  message. A member mentioned by object ID wasn't found: the group-chat roster call names the
  object ID `objectId` where the paged roster says `aadObjectId`; fixed by reading both.
- **Delivery events (D10).** The processor's `NotificationDelivered` events, and its warnings,
  went missing for whole batches: Flex scaled the queue trigger's instance in before the
  buffered telemetry was sent. Fixed by flushing at the end of every invocation.

A third round, after those fixes:

- **The group chat by object ID (2).** The member is now mentioned.
- **Delivery events (D10).** Every queued message has its `NotificationQueued` and
  `NotificationDelivered` event, and the warnings arrive.
- **A tag ID that isn't the tag's (2).** Teams answers `400` with `BadArgument`: "Mentioned Tag
  with id … does not exist in current Team", and the bot posts the message with the tag as plain
  text. The ID tried was built from the short tag ID the Teams client shows, in the format
  Microsoft's documentation examples suggest; those examples are placeholders, and a Graph tag ID
  can't be derived that way. It has to come from Graph (`GET /teams/{id}/tags`).

A fourth round: **a real tag (2).** The bot's `ids` command (added for this) showed the ID Teams
sends when someone mentions the tag: base64 of the tenant ID, the team ID and the short tag ID, in
that order, the reverse of what the documentation examples suggested for the first two (an
observation, not a documented format). A notification mentioning the tag with that ID, then a
person, rendered both as mentions, with nothing reported unresolved. Teams' incoming tag mentions
carry no `"type": "tag"`, so `ids` tells a tag by its ID, which is neither a `29:` user ID nor a
`28:` bot ID.

Still to verify:

- A direct message to someone with no chat with the bot at all, so that Teams creates a new one
  rather than returning the existing one, as it did above (3). On dev, every current member of
  the team the bot is in had the app installed personally months ago, so this needs someone new.
- A shared channel (2).

## 10. Out of scope

- **`Action.OpenUrl` allow-list:** withdrawn by the client.
- **Aliases as code** and **per-alias authorization:** not now (§2). `target` in the status
  lets a caller check that a delivery went where it expected.
- **Escalation, reminders, digests and deduplication:** the caller's job.
