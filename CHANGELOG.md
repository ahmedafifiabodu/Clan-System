# Changelog

All notable changes to the Clan System project. Dates reflect when work happened in-session, not
calendar releases.

## [Unreleased]

### Fixed
- Emoji picker rendered one emoji per row instead of a wrapping grid. `.emoji-grid > .unity-scroll-view__content-container`
  matched nothing — Unity's `ScrollView` nests its real content container two levels deep, inside
  `unity-content-viewport`, under class `unity-scroll-view__content-container`, not as a direct
  child. Selector fixed to `.emoji-grid .unity-scroll-view__content-container` (descendant, not
  child). Verified live: grid now wraps horizontally.
- Removed `Assets/ClanSystem/README.md`, a stale duplicate from the initial commit with no
  screenshots and out-of-date copy. The real README — with the setup guide, security model and
  screenshots — has only ever been the one at the repo root.
- Recaptured `docs/images/clan-tab.png`, `chat-emoji.png`, `leaderboard-players.png`,
  `leaderboard-clans.png` against the live backend after the moderation-gate and Vivox fixes.
  `friends-tab.png` left as-is — the Friends service was reporting unavailable this session
  (live UGS issue, not code), so a recapture would have shown a broken state instead of a
  populated one.

### Added
- `SOCIAL_ModerationAction.js` — Cloud Code target for the dashboard "Moderation actions" hook.
  Maps the five default actions (Ban from game, Block from voice & chat, Mute All, Text Mute,
  Voice Mute) onto per-player text/voice/game restrictions in Cloud Save; clears them on lift.
- `SOCIAL_ModerationAutomation.js` — Cloud Code target for the dashboard "Automation" hook.
  Records every Safe Text incident per player, escalates restriction duration on repeat offences
  (severity → minutes, doubling per prior offence, capped at 30 days). Never issues a permanent
  ban from automation alone.
- Moderation enforcement wired into `SOCIAL_VivoxToken.js` — a muted or banned player is refused a
  Vivox token outright, so the restriction holds regardless of client behaviour.
- `Assets/ClanSystem/Tests/PlayMode/SocialBackendTests.cs` — first automated Test Runner fixture,
  own asmdef (`ClanSystem.Tests.PlayMode`), 7 Play Mode tests against the real backend (no mocks):
  sign-in, clan creation/ownership, duplicate-clan rejection, self-role-change rejection, score
  submission reaching both leaderboards, clan search, disband.
- `Assets/ClanSystem/Runtime/Services/VivoxCommunicationService.cs`: `SemaphoreSlim` voice gate —
  channel joins/switches now serialise instead of racing when multiple requests land in one frame.
- Disband now attempts a real leaderboard entry delete via the Leaderboards Admin API
  (`DELETE .../scores/players/{playerId}`), falling back to zeroing the entry if the delete is
  rejected (pending an Admin-role service account).
- `README.md`, `GAME.md`, this changelog.
- `docs/images/` — five real screenshots (clan, chat+emoji, both leaderboards, friends).

### Fixed
- **Vivox re-login race** — `VivoxCommunicationService.Dispose()` fired `LogoutAsync()` without
  awaiting it, so a subsequent sign-in could begin mid-logout (`LoginSession: Invalid State - must
  be logged out to perform this operation`). `Dispose()` now publishes the logout task and
  `LoginAsync` awaits it before logging in, then polls `IsLoggedIn` until the SDK has genuinely
  dropped the session (`LogoutAsync` returns before teardown completes). The task is held in a
  **static** field on purpose: `VivoxService.Instance` is a process-wide singleton, so the login
  that races the logout runs on a *different* service instance and could never see an instance
  field. Also closes any orphaned session nobody is tearing down (reloaded scene, failed dispose).
- **Leave-before-join** — `SyncClanChannelAsync` could call `LeaveChannelAsync` on a channel that
  was never joined (`Unable to call LeaveChannelAsync because you are not currently in the specified
  target channel`). Root cause: `_clanChannelName` is assigned unconditionally, while the join is
  gated on `IsLoggedIn` and can be refused by the token server — leaving a name with no channel
  behind it. Guard placed inside `LeaveChannelAsync` so every caller benefits: it first awaits any
  join still in flight for that channel (otherwise the join lands just after the leave and re-adds
  it), then leaves only if the channel is actually joined per the local sets or the SDK's
  `ActiveChannels`.
- **Authentication profile switching was a silent no-op** — `UgsAuthenticationGateway.InitializeAsync`
  only called `SwitchProfile` when already signed *out*, but nothing signs out between uses. After
  the first sign-in every later call therefore kept the previous player, and `SignInAsync`
  short-circuited on `IsSignedIn`. All 7 tests ran as a single player and 5 of them failed on the
  per-player 60s clan-create cooldown (`You created a clan recently. Try again later.`). It now
  compares against `AuthenticationService.Instance.Profile` and signs out first when a different
  profile is requested, since `SwitchProfile` throws while signed in. Cached credentials are kept,
  so a profile still maps to a stable player id. Affects multi-client account switching in the
  demo, not only tests.
- `SocialBackendTests` is green: **7/7 passing** against the live backend.

### Fixed earlier this session
- Voice channel capability could not be changed in place (`Unable to join channel "global" because
  there is already an active channel with the same name`, followed by `ArgumentException` /
  `Sequence contains no matching element` inside the Vivox SDK). Root cause: leaving and rejoining a
  channel to upgrade text → voice. Rewritten to join once with `TextAndAudio` and switch the
  microphone via `SetChannelTransmissionModeAsync` instead of rejoining.
- Removed an auto-unmute on joining voice that silently contradicted `JoinVoiceMutedByDefault = true`.
- `[CloudCodeAuthoring]: NPM was initialized by a package version that does not match...` — root
  `package.json` was missing `ccaVersion`; added.
- Vivox channel-name parsing didn't account for UGS appending the environment id
  (`global.{envId}@domain`), causing every join to be refused as an "Unknown channel". Parser now
  extracts the segment between issuer and environment id.
- Chat history call could throw `Object reference not set to an instance of an object` when invoked
  before the channel was joined; now returns empty instead.

### Security
- **Exposed Vivox signing key removed** before any push. The key had only ever existed in the
  working tree (never committed — verified via `git log`), so no history rewrite was needed.
  `SOCIAL_VivoxToken.js` now requires `VIVOX_ISSUER` / `VIVOX_KEY` from Secret Manager and fails
  closed (`VOICE_NOT_CONFIGURED`) if absent — no in-script fallback key.
- Verified live: requesting a Vivox token for a clan the caller does not belong to returns
  `PERMISSION_DENIED`, no token issued.
- Verified live: voice reconnects successfully on Secret Manager credentials alone (key removal
  didn't break anything).

### Known issues (open)
- `SyncClanChannelAsync` leaves the previous clan channel without clearing `ActiveVoiceChannel`, so
  `IsVoiceJoined(Clan)` reports true for the *new* channel before anything transmits into it.
  Cosmetic in the voice bar; not covered by the current tests.
- The same method signals "clear the clan buffer" by firing `MessageReceived?.Invoke(null)` — every
  subscriber receives a null `CommMessage` and has to know to expect it.
- `SocialBackendTests` is not re-runnable back to back: profiles map to stable player ids, so a
  second run within 60s hits the per-player clan-create cooldown.
- The Friends notification channel reports `notification error code: 23002` on each rapid
  re-sign-in. Non-fatal by design — friends is optional and degrades the UI rather than blocking
  sign-in — but it is noisy in the test log.
- Leaderboard entry deletion on disband falls back to zeroing (not true deletion) until a
  Leaderboards Admin service account exists in Secret Manager.
- Two-client concurrency (kick/leave channel revocation, clan switching, speaking indicators with
  real audio, muting another player, reconnect) still unverified — needs a second live client.
- Dashboard wiring for the two new moderation Cloud Code scripts not yet confirmed applied (scripts
  are deployed and ready; pointing the dashboard hooks at them is a manual step).

### Corrected in-session
- Initially claimed leaderboard score deletion was impossible because the Cloud Code JS SDK
  (`@unity-services/leaderboards-1.1`) has no delete method. Wrong conclusion, right observation:
  the **Leaderboards Admin API** (separate from both the JS SDK and the installed C# client
  `com.unity.services.leaderboards@2.3.4`) does support deletion. Implemented against it.

---

## Earlier (pre-changelog)

Established the whole system before this file existed:

- Server-authoritative clan CRUD, roles (Owner/Officer/Member), invites, join requests, activity
  log — Cloud Code + Cloud Save private custom data.
- Player and clan leaderboards — Cloud Code writes only, client never calls the score API directly.
- Friends via UGS Friends service, merged with clan info from Cloud Code.
- Chat + voice migrated from a Cloud Save polling implementation to Vivox (one transport for both,
  push delivery, participant/speaking state) — the Cloud Save version couldn't do voice and
  serialised every chat message through a single write-locked item.
- Emoji picker + `EmojiDatabase` ScriptableObject catalogue, `:shortcode:` expansion.
- UI Toolkit demo: Friends / Clan / Chat+Voice / Leaderboards / Notifications tabs, voice bar with
  connection state, per-player mute/volume, speaking indicators.
- `SocialSmokeTest` manual smoke test, 29 checks, last clean run 29/29 before the moderation-gate
  change (needs rerun given the new open bugs).
- Initial GitHub push (`aed58ef`) — 143 files.
