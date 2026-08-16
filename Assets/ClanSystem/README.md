# Clan / Social System (Unity Gaming Services)

A server-authoritative clan, chat, friends and leaderboard system built on Unity Gaming Services.
Every state change is executed by Cloud Code; the Unity client is treated as untrusted.

Verified end to end against the live `Clan System` UGS project (`60d3ba05-1508-4323-b795-aca4b31e5bfe`).

---

## 1. Service map

| Responsibility | Service | Why |
| --- | --- | --- |
| Sign-in, player identity | Authentication 3.7.4 (anonymous + profiles) | Profiles give separate player ids on one machine, which is what makes local multi-client testing possible. |
| Display names | Player Names (part of Authentication) | Official name service; the server reads it rather than trusting a client-supplied name. |
| Friends, presence, invite nudges | Friends 1.2.0 | Official social graph. Presence and relationship events come from the service. |
| Clan records, rosters, invites, join requests | Cloud Save **private custom data** | Private custom data is unreachable with a player token. A client cannot read another clan's roster even by forging requests - only Cloud Code can touch it. |
| All mutations and permission checks | Cloud Code 2.10.4 (JS scripts) | Single choke point for authority, validation, rate limiting and optimistic-concurrency retries. |
| **Text and voice chat** | **Vivox 16.11.0** | One transport for both, with push delivery, server-held history, participant presence and speaking state. Replaced the earlier Cloud Save chat, which could not do voice and serialised every message through a single item. |
| Player and clan rankings | Leaderboards 2.3.4 | Real ranking service. Scores are written **only** from Cloud Code; the clan board uses the clan id as its entry id. |

### Why chat moved to Vivox

The original Cloud Save chat could not satisfy the requirements: no voice at all, no push (3s polling),
no participant or speaking state, and every message in a channel rewrote one Cloud Save item under a
write lock, which serialises and then fails under concurrent senders. Vivox provides all of it, so
both channels now run on one transport instead of two unrelated chat systems.

### Vivox channel security

Channels are `global` and `clan_<clanId>`. Vivox refuses any join without a signed token, and the
signing key exists only in Secret Manager, reachable only by `SOCIAL_VivoxToken.js`.

`CloudCodeVivoxTokenProvider` is registered with `VivoxService.Instance.SetTokenProvider(...)` before
login, so **every** token - login and each join - is minted server-side. For a clan channel the script
reads the caller's real `clanId` from private custom data and rebuilds the only channel name it will
authorise. Editing a clan id in the client therefore yields no token and no join. The script also
rejects `kick`/`mute` actions from clients and refuses to mint a token whose "from" URI is a different
player.

Membership changes propagate through `SocialState.ClanMembershipChanged`, which the coordinator uses
to leave the old clan channel and join the new one on join, leave, kick and clan switch. Because
tokens are single-use and short-lived, a removed player cannot obtain a new one.

This matches the pattern Unity recommends for guilds: a custom item per clan, a `members` key on it,
and Cloud Code performing add/remove.

## 2. Architecture

Three assemblies enforce a one-way dependency chain (Presentation → Services → CoreData):

```
Assets/ClanSystem/
  CloudCode/                    server-authoritative JS + leaderboard definitions
    SOCIAL_ClanCommand.js       create/join/leave/invite/kick/promote/transfer/disband/settings
    SOCIAL_ClanQuery.js         snapshot, clan detail, roster, search, activity, join requests
    SOCIAL_Chat.js              global + clan chat send/fetch, rate limiting
    SOCIAL_Leaderboards.js      score submission (clamped) and both leaderboards
    player_score.lb             leaderboard definition (Desc, KeepBest)
    clan_score.lb               leaderboard definition (Desc, KeepLatest)
  Runtime/
    CoreData/       (ClanSystem.CoreData)     models, enums, results, config asset
    Services/       (ClanSystem.Services)     Cloud Code backend, auth + friends gateways, state, coordinator
    Presentation/   (ClanSystem.Presentation) UI Toolkit controllers, bootstrap, smoke test
  UI/               SocialWindow.uxml / .uss / SocialPanelSettings.asset
  Scenes/SocialDemo.unity
  ClanSystemConfig.asset        every tunable: script names, leaderboard ids, limits, poll intervals
```

`ISocialBackend`, `IAuthenticationGateway` and `IFriendsGateway` are seams: the UI and the
coordinator never talk to a UGS SDK directly.

## 3. Data model

Cloud Save private custom data (server-only):

| Custom id | Key | Contents |
| --- | --- | --- |
| `clan-{clanId}` | `profile` | name, tag, description, motd, owner, createdAt, memberCount, maxMembers, isPublic, emblem, score, xp, level |
| `clan-{clanId}` | `members` | map playerId → name, role, joinedAt, contribution, lastActive |
| `clan-{clanId}` | `chat` | ring buffer of the last 100 clan messages |
| `clan-{clanId}` | `activity` | last 50 activity entries |
| `clan-{clanId}` | `requests` | pending join requests |
| `index` | `clans` / `tags` | clan directory for search, plus tag-uniqueness reservations |
| `chat-global` | `messages` | ring buffer of the last 100 global messages |
| `player-{playerId}` | `social` | clanId, role, score, contribution, lastActive |
| `player-{playerId}` | `invites` | invitations addressed to this player |
| `player-{playerId}` | `rate` | chat / score / clan-creation rate counters |

**Concurrency.** Every mutation is read → modify → conditional write using the Cloud Save
`writeLock`. A 409 conflict re-reads and replays the change (up to 4 attempts), so two players
acting on the same clan at once cannot clobber each other. Clan score is *derived* from the roster
(`sum(member.contribution)`) rather than incremented, so it can never drift from its members.

## 4. Security model

Assume a fully compromised client. It cannot:

- grant itself a role - roles live in private custom data and only `setRole`/`transferOwnership` change them, both gated on the caller being Owner;
- join or modify an arbitrary clan - membership is written by the server from `context.playerId`;
- read another clan's chat or roster - both are private custom data, and clan chat resolves the clan from the caller's own membership record (the client never sends a clan id for chat);
- accept somebody else's invitation - invites are stored under the **receiver's** record and the id is only resolvable there, plus `receiverId === context.playerId` is re-checked;
- write a leaderboard score - the client never calls `AddPlayerScoreAsync`; Cloud Code submits totals it computed, and per-submission deltas are clamped (`maxScoreDelta`);
- spam - chat has a minimum interval, a sliding window cap and duplicate-message suppression; clan creation has a cooldown; score submission has a minimum interval.

Client-side role checks exist only to hide controls. The server rejects the same calls regardless.

## 5. Dashboard configuration

Already deployed from the Editor (Deployment package), no action needed:

- Cloud Code scripts `SOCIAL_ClanCommand`, `SOCIAL_ClanQuery`, `SOCIAL_Chat`, `SOCIAL_Leaderboards`
- Leaderboards `player_score` and `clan_score`

**REQUIRED before voice or chat will work — Vivox credentials.**

Vivox is the only part of this system that cannot be configured from the Editor, because it needs
credentials only your dashboard can issue. Until this is done the UI shows `VOICE NOT CONFIGURED`
and chat stays offline; everything else (clans, invites, roles, leaderboards) works regardless.

1. Unity Dashboard → your project → **Vivox** → enable it. Open **Credentials** and copy the
   **Issuer** and the **Signing key/secret** for the environment you are using.
2. Unity Dashboard → **Secret Manager** → add two secrets in the same environment:
   - `VIVOX_ISSUER` = the Vivox issuer
   - `VIVOX_KEY` = the Vivox signing key
   Names must match exactly; `SOCIAL_VivoxToken.js` reads them via `secretManager.getSecret(...)`.
3. Press Play and sign in. The voice bar should read `VOICE READY`.

Do not put the signing key in the Unity project or in `ClanSystemConfig`. The client never sees it,
which is the whole point of minting tokens in Cloud Code.

**Still worth doing manually in the Unity Dashboard (hardening, not required to run):**

1. **Leaderboards → player_score / clan_score → write access:** restrict updates to server/Cloud Code
   only, so a modified client cannot call the Leaderboards client SDK directly. The code never does,
   but the service permits it by default.
2. **Friends:** enable the Friends service for the environment if the Friends tab shows
   "Friends service unavailable". Everything else works without it.
3. Re-deploying after editing a `.js`: **Window → Deployment**, select the items, press Deploy.

**Note on in-script parameters:** Cloud Code extracts `module.exports.params` by running Node, and it
only does so when a `package.json` exists at the project root. That file is committed for this reason -
deleting it makes every script deploy with an empty parameter list and all calls fail with
`Unsupported action 'undefined'`. `module.exports.params` must also stay *after* the
`module.exports = async ...` assignment, otherwise it is overwritten.

## 6. Running the demo

1. Open `Assets/ClanSystem/Scenes/SocialDemo.unity` and press Play.
2. Enter a profile name (default `default`) and press **Sign in anonymously**.
3. Tabs: Friends, Clan, Chat, Leaderboards, Notifications. **Play a match** submits a clamped score;
   **Rename** sets your player name through the Name service.

### Testing with multiple players

The profile field is the mechanism: each profile is a distinct UGS player on the same machine.

- **Sequentially (no build needed):** sign in as `playerA`, create a clan, note your player id from
  the header. Stop, press Play, sign in as `playerB` - now a different player. To exercise invites,
  send the invite from A, then sign in as B and accept it in **Notifications**.
- **Truly side by side:** make a standalone build (the demo scene is already scene 0) and run the
  build next to the Editor, using a different profile in each.
- Friend requests use names, e.g. `PlayerName#1234`, shown in each client's header.

### Automated smoke test

`SocialSmokeTest` (in `Runtime/Presentation`) drives the real backend end to end and logs a
pass/fail report: sign-in, clan creation, ownership, permission rejection, MOTD, global and clan
chat, rate limiting, clan chat history, score submission, both leaderboards, clan search, activity
log, invites and disband. Attach it to an empty GameObject, assign `ClanSystemConfig`, set a profile
and tick **Run On Start**. Last run: **21/21 passed**.

## 7. Known limitations and next steps

- **Chat is polled** (3s) rather than pushed. For production, move to Cloud Code player messages
  (`SubscribeToPlayerMessagesAsync`) or Vivox text channels to cut latency and request volume.
- **Clan search scans a single index item.** Fine into the low thousands of clans; beyond that,
  switch to a Cloud Save index with a dashboard-configured query, which Unity supports for exactly
  this case.
- **Disbanded clans leave a zeroed leaderboard entry.** They are filtered out server-side when the
  clan board is built, but the entries are not deleted.
- **Friends presence is coarse** - the demo publishes a single "In the social demo" activity.
- **Chat has no moderation** beyond length, rate and control-character stripping. Add a word filter
  or Safe Text before shipping.
- **No automated Play Mode test assembly.** The smoke test is a component, not a Unity Test Runner
  fixture; converting it would let it run in CI.
