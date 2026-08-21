# Clan System — Unity Gaming Services

A server-authoritative clan, chat, voice, friends and leaderboard system for Unity 6, built on Unity
Gaming Services. Every state change runs in Cloud Code; the client is treated as untrusted.

Unity **6000.3.10f1** · URP · UI Toolkit

---

## What it does

- **Clans** — create, join, leave, invite, accept/reject, kick, promote/demote, transfer ownership, disband
- **Roles** — Owner / Officer / Member, with permissions enforced server-side
- **Text chat** — global and clan channels, with an emoji picker and `:shortcode:` expansion
- **Voice chat** — global and clan voice, mic/speaker toggles, per-player mute and volume, speaking indicators
- **Friends** — relationships and presence from the Friends service, merged with clan info
- **Leaderboards** — player rankings and clan rankings, both written only by the server
- **Notifications** — clan invitations, friend requests and join requests

---

## Screens

### Clan

Clan dashboard: emblem, tag, level, leader, member roster with roles and contribution, activity feed,
and the leader-only actions. Officers and members see a reduced set — and the server rejects the rest
regardless of what the UI shows.

![Clan tab](docs/images/clan-tab.png)

### Chat and voice

One page for both. The voice bar carries connection state, channel switching, mic and speaker
toggles, and the live participant list with speaking indicators. Below it, `GLOBAL` / `CLAN` text
channels with an emoji picker.

![Chat with emoji picker](docs/images/chat-emoji.png)

### Leaderboards

Player rankings, with the caller's own row pinned at the bottom even when it falls outside the page.

![Player leaderboard](docs/images/leaderboard-players.png)

Clan rankings, showing member counts alongside score.

![Clan leaderboard](docs/images/leaderboard-clans.png)

### Friends

Friends on the left with presence and clan tags, the caller's own clan roster on the right.

![Friends tab](docs/images/friends-tab.png)

---

## Service map

| Responsibility | Service | Why |
| --- | --- | --- |
| Sign-in, player identity | Authentication 3.7.4 (anonymous + profiles) | Profiles give separate player ids on one machine, which is what makes local multi-client testing possible |
| Display names | Player Names | The server reads names from the name service rather than trusting the client |
| Friends, presence | Friends 1.2.0 | Official social graph and presence events |
| Clan records, rosters | Cloud Save **private custom data** | Unreachable with a player token — a client cannot read another clan's roster even by forging requests |
| Per-player state (membership, invites, cooldowns, mutes) | Cloud Save **protected player data** | The player may read their own record but never write it, and it is scoped to the player, so it is deleted with them |
| Clan directory (browse, search) | Cloud Save **query index** over the clan items | One query instead of a fan-out, and no second copy of each clan that can drift from its profile |
| All mutations, permissions | Cloud Code 2.10.4 (JS) | One choke point for authority, validation, rate limiting and write-lock retries |
| Text and voice chat | Vivox 16.11.0 | One transport for both, with push delivery, server-held history, participant presence and speaking state |
| Rankings | Leaderboards 2.3.4 | Real ranking service; scores written only from Cloud Code |

### Why chat is on Vivox

An earlier version stored chat in Cloud Save. It could not do voice, had no push (3s polling), no
participant or speaking state, and every message rewrote a single Cloud Save item under a write lock —
which serialises and then fails under concurrent senders. Vivox provides all of it, so text and voice
now share one transport instead of two unrelated chat systems.

---

## Security model

Assume a fully compromised client. It cannot:

- **Grant itself a role.** Roles live in private custom data; only `setRole` / `transferOwnership` change them, both gated on Owner.
- **Join or modify an arbitrary clan.** Membership is written by the server from `context.playerId`.
- **Read another clan's chat or roster.** Both are server-side only, and clan chat resolves the clan from the caller's own membership record — the client never sends a clan id for chat.
- **Join another clan's voice channel.** Vivox refuses any join without a signed token, and the signing key exists only in Secret Manager, reachable only by `SOCIAL_VivoxToken.js`. That script rebuilds the permitted channel name from the caller's real clan, so a tampered clan id yields no token.
- **Accept somebody else's invitation.** Invites are stored under the receiver's record, and `receiverId === context.playerId` is re-checked.
- **Write a leaderboard score.** The client never calls `AddPlayerScoreAsync`; Cloud Code submits totals it computed, and per-submission deltas are clamped.
- **Spam.** Chat has a minimum interval, a sliding-window cap and duplicate suppression; clan creation has a cooldown.

Client-side role checks exist only to hide controls. The server rejects the same calls regardless.

Verified against the live backend — requesting a token for a clan the player does not belong to:

```
ok: False | code: PERMISSION_DENIED | message: You are not a member of that clan.
token issued: False
```

---

## Layout

Three assemblies enforce a one-way dependency chain (Presentation → Services → CoreData):

```
Assets/ClanSystem/
  CloudCode/                    server-authoritative JS + leaderboard definitions
    SOCIAL_ClanCommand.js       create/join/leave/invite/kick/promote/transfer/disband/settings
    SOCIAL_ClanQuery.js         snapshot, clan detail, roster, search, activity, join requests
    SOCIAL_Leaderboards.js      score submission (clamped) and both leaderboards
    SOCIAL_VivoxToken.js        mints Vivox tokens against real clan membership; also the moderation gate
    SOCIAL_ModerationAction.js  dashboard "Moderation actions" hook, applies/lifts restrictions
    SOCIAL_ModerationAutomation.js  dashboard "Automation" hook, escalates on repeat Safe Text incidents
    player_score.lb             leaderboard definition (Desc, KeepBest)
    clan_score.lb               leaderboard definition (Desc, KeepLatest)
  Runtime/
    CoreData/       models, enums, results, config asset, emoji database
    Services/       Cloud Code backend, auth/friends/Vivox gateways, state, coordinator
    Presentation/   UI Toolkit controllers, bootstrap, smoke test
  UI/               SocialWindow.uxml / .uss / SocialPanelSettings.asset
  Scenes/SocialDemo.unity
  ClanSystemConfig.asset        every tunable: script names, leaderboard ids, limits, channels
  EmojiDatabase.asset           emoji catalogue used by the picker
```

`ISocialBackend`, `IAuthenticationGateway`, `IFriendsGateway` and `ICommunicationService` are seams —
the UI never touches a UGS SDK directly.

---

## Setup

The project is already linked to a UGS project, and the Cloud Code scripts and leaderboards are
deployed. To run it against your own UGS project:

1. **Link the project** — Project Settings → Services.
2. **Vivox credentials** — Dashboard → Vivox → Credentials, then add two secrets in
   **Secret Manager** (Cloud Code service access):
   - `VIVOX_ISSUER` — the Vivox token issuer
   - `VIVOX_KEY` — the Vivox signing key

   The key must never be committed or placed in a Unity asset. A Unity asset ships inside the game
   binary, where anyone could extract it and mint a token for any channel.
3. **Deploy Cloud Code and leaderboards** — Window → Deployment, select all, Deploy.
4. **Enable Friends** for the environment if the Friends tab reports it unavailable.

Optional hardening: set both leaderboards to server-only write access in the Dashboard. The client
never writes scores, but the service permits it by default.

**Note on `package.json`:** the root `package.json` is required. Cloud Code extracts
`module.exports.params` by running Node, and only does so when a Node project exists at the project
root. Delete it and every script deploys with an empty parameter list, so all calls fail with
`Unsupported action 'undefined'`.

---

## Running the demo

1. Open `Assets/ClanSystem/Scenes/SocialDemo.unity` and press Play.
2. Enter a profile name and press **Sign in anonymously**.
3. Tabs: Friends, Clan, Chat, Leaderboards, Notifications. **Play a match** submits a clamped score;
   **Rename** sets your player name.

### Testing with multiple players

Each Authentication profile is a distinct UGS player on the same machine.

- **Sequentially:** sign in as `playerA`, create a clan, note the player id in the header. Stop, Play
  again, sign in as `playerB`. To test invites, send from A, then sign in as B and accept in
  **Notifications**.
- **Side by side:** make a standalone build (the demo scene is scene 0) and run it next to the
  Editor with a different profile in each.

### Automated smoke test

`SocialSmokeTest` drives the real backend end to end and logs a pass/fail report: sign-in, clan
creation, ownership, permission rejection, MOTD, global and clan text, emoji, global and clan voice,
mic/speaker mute, chat history, score submission, both leaderboards, clan search, activity log,
invites and disband. Attach it to an empty GameObject, assign `ClanSystemConfig`, set a profile and
tick **Run On Start**.

Last run: **29/29 passed** (before the moderation-gate change — needs a rerun).

### Automated Play Mode tests

`Assets/ClanSystem/Tests/PlayMode/SocialBackendTests.cs`, own asmdef, 7 tests against the real
backend (no mocks): sign-in, clan creation/ownership, duplicate-clan rejection, self-role-change
rejection, score submission reaching both leaderboards, clan search, disband. Run via Test Runner →
Play Mode.

Last run: **7/7 passed.** Not re-runnable back to back — Authentication profiles map to stable
player ids, so a second run inside 60s hits the per-player clan-create cooldown.

---

## Known limitations

- **Not yet verified with two simultaneous live clients:** channel revocation on kick/leave, clan
  switching, speaking indicators with real audio, muting another player, and reconnect behaviour.
  The code paths exist (`SocialState.ClanMembershipChanged` drives channel resync) but are untested
  under real concurrency.
- **Disband deletes the clan's leaderboard entry** via the Leaderboards Admin API. Cloud Code's own
  `context.serviceToken` does not carry the *Leaderboards Admin* role, so it falls through to a
  service account whose key pair (`UGS_SA_KEY_ID` / `UGS_SA_SECRET_KEY` in Secret Manager) is sent
  as Basic auth. Verified live: real deletion, `leaderboardRemovalMethod: "serviceAccount"`. If the
  credentials are ever removed it degrades to zeroing the entry, which the clan board filters out
  but does not remove.
- **Renaming briefly rebuilds the chat session.** Vivox stamps the sender name into each message
  from its login session and offers no setter, so a rename logs out and back in under the new name,
  restoring channels, microphone and speaker state. Messages already sent keep the name they were
  sent under - server-held history is not rewritten.
- **No profanity filter in the old sense.** Vivox Safe Text owns that now, gated through the two
  moderation Cloud Code scripts above; dashboard wiring for both is not yet confirmed applied.
- **`runInBackground` must stay enabled.** With it off, every async request freezes whenever the
  Editor loses focus.
