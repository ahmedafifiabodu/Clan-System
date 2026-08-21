# Clan System — Project Notes

Unity **6000.3.10f1** · URP · UI Toolkit · Unity Gaming Services

Server-authoritative clan, chat, voice, friends and leaderboard system. The client is treated as
untrusted throughout — every mutation and every channel-access decision is made in Cloud Code, never
in the client.

See [README.md](README.md) for the full service map, security model, setup steps and screenshots.
This file tracks project-level state: what's live, what's pending, what's broken.

---

## Live services

| Service | Version | Role |
| --- | --- | --- |
| Authentication | 3.7.4 | anonymous sign-in + profiles (multi-client testing) |
| Cloud Save | private custom data | clan records, rosters, invites, moderation records |
| Cloud Code | 2.10.4 | all mutations, permission checks, rate limits |
| Vivox | 16.11.0 | global + clan text and voice, one transport |
| Leaderboards | 2.3.4 | player and clan rankings, server-written only |
| Friends | 1.2.0 | relationships and presence |

UGS project: `60d3ba05-1508-4323-b795-aca4b31e5bfe`, environment `f31f047c-f136-4dd0-a7a3-1a01f119aa4a`
(`production`). GitHub: `ahmedafifiabodu/Clan-System`.

## Cloud Code scripts (all deployed)

- `SOCIAL_ClanCommand.js` — create/join/leave/invite/kick/promote/transfer/disband/settings
- `SOCIAL_ClanQuery.js` — snapshot, roster, search, activity, join requests
- `SOCIAL_Leaderboards.js` — clamped score submission, player + clan boards
- `SOCIAL_VivoxToken.js` — mints Vivox tokens against real clan membership; also the moderation gate
- `SOCIAL_ModerationAction.js` — dashboard "Moderation actions" hook, applies/lifts restrictions
- `SOCIAL_ModerationAutomation.js` — dashboard "Automation" hook, escalates on repeat Safe Text incidents

## Dashboard configuration required (not doable from the Editor)

1. **Secret Manager**: `VIVOX_ISSUER`, `VIVOX_KEY` — done, confirmed working live.
2. **Vivox Safe Text → Automation**: point at `SOCIAL_ModerationAutomation.js` — script deployed,
   dashboard wiring not yet confirmed done.
3. **Vivox Safe Text → Moderation actions**: point at `SOCIAL_ModerationAction.js` — same, script
   deployed, dashboard wiring not yet confirmed done.
4. **Leaderboards write access** → server-only (hardening, not required to run).
5. ~~**Leaderboards Admin service account.**~~ **Done and verified live.** Service account holds
   *Leaderboards Admin* (project role); `UGS_SA_KEY_ID` / `UGS_SA_SECRET_KEY` are in Secret Manager.
   A disband with a real score returned `leaderboardRemovalMethod: "serviceAccount"` and the entry
   was genuinely removed from the clan board. Note the key pair is sent as **Basic** auth directly;
   exchanging it for a Bearer token first returns 401. Disband calls
   `DELETE .../leaderboards/{id}/scores/players/{playerId}`; if the credentials are ever removed it
   degrades to zeroing the entry, and `leaderboardRemovalMethod` in the response says which ran.

## Known bugs

None open. Two more fixed since:

0a. **Chat page overlap** — `GLOBAL`/`CLAN` tabs drew over the voice participants label. Cause was
   default `flex-shrink: 1` on every chat-column row plus UI Toolkit not clipping overflow, so a
   squeezed row painted over its neighbour. Fixed chrome is now `flex-shrink: 0` and `.chat-list`
   is the only row that yields.
0b. **Rename not reflected in chat** — Vivox bakes the display name into the login session, so
   `LoginOptions.DisplayName` was the real source of truth. Rename now rebuilds the session under
   the new name and restores channels, voice and mute state.

The three found by the Play Mode fixture (`SocialBackendTests`) are fixed:

1. **Vivox re-login race** — `Dispose()` fired `LogoutAsync()` without awaiting it, so a second
   sign-in could start mid-logout: `LoginSession: Invalid State - must be logged out to perform
   this operation`. `Dispose()` now publishes the logout task and `LoginAsync` waits on it before
   logging in, then polls until the SDK really has dropped the session. The task is **static**
   because `VivoxService.Instance` is a process-wide singleton — the racing login runs on a
   different service instance. Affected real re-sign-in, not just tests.
2. **Leave-before-join** — `SyncClanChannelAsync` could call `LeaveChannelAsync` on a channel never
   joined: `Unable to call LeaveChannelAsync because you are not currently in the specified target
   channel`. Cause: `_clanChannelName` is assigned unconditionally while the join is gated on
   `IsLoggedIn` and can be refused by the token server. `LeaveChannelAsync` now awaits any join
   still in flight, then leaves only if the channel is really joined.
3. **Profile switching was a silent no-op** — `UgsAuthenticationGateway.InitializeAsync` only called
   `SwitchProfile` when already signed *out*, so after the first sign-in every later call kept the
   previous player and `SignInAsync` short-circuited. Every test ran as one player and tripped the
   per-player 60s clan-create cooldown. It now signs out first when a different profile is asked
   for. Also affects multi-client account switching in the demo, not just tests.

## Known limitations (by design, not bugs)

- Voice-channel switches are now serialised (`SemaphoreSlim` gate) — no longer a limitation, fixed.
- Clan search matches a name *prefix*, not a substring. The directory is a Cloud Save query index
  now, and queries compare rather than search, so `OLF` no longer finds `WOLFPACK`. Exact tag match
  still works on any tag and outranks name hits. The old fan-out could substring-match only because
  it had already loaded every clan into memory, which is the part that did not scale.
- Search reports `hasMore` rather than a true result count. A query does not say how many rows it
  could have returned, so `total` is what the caller has actually been shown.
- Cloud Save has no list-all API for custom items, so nothing in the backend can enumerate every
  clan or player. Any bulk operation over existing data has to be driven from the Dashboard or the
  UGS CLI, which is why the environment is wiped rather than migrated when the layout changes.
- No profanity filter in the old sense — Vivox Safe Text now owns that; see moderation scripts above.
- `Application.runInBackground` must stay `true` — with it off, async requests freeze when the Editor
  loses focus. Already set in ProjectSettings.

## Testing

- **Automated**: `Assets/ClanSystem/Tests/PlayMode/SocialBackendTests.cs`, 7 tests against the real
  backend (sign-in, create, duplicate-clan rejection, self-role-change rejection, score + both
  leaderboards, search, disband). Run via Test Runner → Play Mode. **Currently green, 7/7.**
  Note the suite is not re-runnable back to back: profiles map to stable player ids, so a second
  run inside 60s hits the per-player clan-create cooldown. Wait a minute between runs.
- **Manual smoke test**: `SocialSmokeTest` component, 29 checks, last clean run 29/29 (before the two
  bugs above were introduced by the moderation-gate change — needs a rerun).
- **Two-client verification**: not yet done. Needed for kick/leave channel revocation, clan switching,
  speaking indicators with real audio, muting another player, reconnect behaviour.

## Next steps, in order

1. Rerun `SocialSmokeTest` to confirm 29/29 still holds.
2. Two-client pass (Multiplayer Play Mode or a second build).
3. Confirm dashboard wiring for the two moderation Cloud Code hooks.
4. Provision the Leaderboards Admin service account for real disband deletion.
5. Commit and push.
