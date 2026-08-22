# Changelog

All notable changes to the Clan System project. Dates reflect when work happened in-session, not
calendar releases.

## [Unreleased]

### Fixed
- **The voice roster showed nobody until you joined voice yourself.** It was drawn only when the
  local player was transmitting, so a player already in global voice was invisible to everyone who
  had not yet pressed Join - which is the one moment that information is worth having. The rail now
  shows the roster of the channel the client is *joined* to, and the client joins global on login.
- **Participant events could be dropped.** The channel a Vivox callback reports is sometimes a name
  and sometimes a full `sip:` URI; an exact comparison against the expected name missed the latter,
  leaving the roster to be corrected by the next poll instead of by the event.
- **A participant row could describe the wrong player.** Rows were rebuilt only when the roster's
  size changed and updated by index otherwise, so somebody leaving as somebody else arrived left
  every row pointing at its neighbour - and a Mute click muting a stranger. Rows are now diffed by
  player id and order.
- **Clan search no longer depends on an index being configured.** The tag lookup filtered on
  `dirPublic` and `dirTag` together, which asks for a compound index that is not in the table -
  a query is served by the index naming its keys, so the service rejected it and took the whole
  search down with it. The tag query now filters on `dirTag` alone and checks public-ness on the
  returned summary. When Cloud Save reports `query does not use an index` at all, browse and
  search fall back to reading the tag reservation shards (`index-t<0..15>`), which are already an
  exact roster of live clans, and rank the results in memory exactly as the indexed path does.
  Creating the indexes turns the fast path back on with no code change.
- **Cloud Save failures inside Cloud Code report their cause.** An axios rejection thrown out of a
  script reaches the client as `problems/invocation/axios`, a discriminator the SDK does not know,
  so every failure degraded to "Unknown". Directory queries now catch their own failure and return
  the status, the service's detail and the index they wanted.
- **Vivox login is retried while the failure is a transport one.** The connector handshake times
  out on a cold network where the HTTP services merely run slow.

### Changed
- **The voice participant row is stacked, not crammed.** The rail is 268px: a name, a mute button
  and a volume slider cannot all hold their minimum width on one line, and flexbox resolved that by
  overlapping them - the mute button sat on top of the name it belonged to. Name on one line,
  controls beneath. The volume readout shows the signed adjustment, because Vivox's local volume is
  a change relative to normal (-50 to 50 around a neutral 0), not a percentage of some maximum.
- **The bottom-right corner is reserved for the floating notification button.** The voice dock keeps
  62px clear on its right, so the button's default resting place never covers Leave.
- **Notifications left the tab bar for a floating button.** A draggable button sits in the
  bottom-right corner by default, snaps to the nearest corner on release with an elastic tween, and
  carries a red unread badge (`9+` past nine) as a child element, so the badge follows every drag
  and snap. Pressing it opens a panel with the three existing categories - clan invitations,
  friend requests and join requests - as tabs; looking at a category marks it read.
- **Status toasts moved to the top centre**, positioned by translation rather than fixed offsets so
  they hold their place at any window size, over the header's empty middle.

## [1.0.0] - 2026-08-22

First tagged release. Installable as a UPM package:
`https://github.com/ahmedafifiabodu/Clan-System.git?path=Assets/ClanSystem`

### Changed
- **Per-player state moved from Private Game Data to Protected Player Data.** `social`, `invites`,
  `rate` and `moderation` were stored as a `player-<id>` custom item, which is one access class too
  strict and, more importantly, the wrong scope: a custom item belongs to the game, so it is never
  removed when the player is deleted, and Cloud Save has no list-all API to find the orphans later.
  Protected Player Data is exactly this data's contract - the player may read their own record,
  only a server may write it - and UGS deletes it along with the player.
- **The clan directory is a Cloud Save query index, not a shard set.** The 16 `index-c*` items are
  gone; browse and search query the `clan-<id>` items directly. This also removes `updateIndex` and
  the drift risk that came with keeping a second copy of every clan summary in step with its
  profile. See the correction below - the reasoning that produced the shards was wrong.
  The tag shards (`index-t*`) stay: two clans racing for the same tag must be serialised, and the
  write lock on the shard item is what does that. A query is a read, and a read cannot reserve.
- **Clan search matches a name prefix, not a substring.** Cloud Save queries compare (`EQ`, `LT`,
  `GE`, ...) - they do not search - so `dirNameUpper GE "WOL"` bounded above is the strongest name
  filter an index can serve. `OLF` no longer finds `WOLFPACK`. Exact tag match works on any tag and
  is ranked above name hits. The old fan-out could substring-match only because it had already
  loaded every clan into memory, which is the part that did not scale.
- **Search reports `hasMore` instead of a true result count.** A query does not report how many
  rows it could have returned, so `total` is now what the caller has actually been shown.
- **Chat page rebuilt around a voice rail.** It was four stacked bands - voice bar, participant
  list, tabs, messages - so ~40% of the page was chrome before the first line of text. The
  conversation now takes the full height with voice in a 268px rail beside it, collapsing to a
  strip above the messages below 640px.
- **Microphone and speaker are icon buttons with real state.** State is carried by colour *and* a
  slash overlay, so it survives colour-blindness and a greyscale screenshot. Microphone strength is
  driven by `VivoxParticipant.AudioEnergy` through the new `ICommunicationService.MicrophoneEnergy`
  - measured level, not an animation.
- **Sign-in no longer waits on services the window does not need.** `StartAsync` awaits only
  authentication and the first snapshot; friends and voice start concurrently once the window
  exists. Measured 15218ms before (services 31ms, auth 2179ms, friends 3416ms, snapshot 876ms,
  voice 8713ms) against 3418ms and 5431ms across two runs after, with voice arriving at 5358ms and
  12896ms in the background.

### Superseded from the previous entry
- **Clan directory sharded across 16 Cloud Save items.** It was a single `index`/`clans` item
  holding every clan, which caps out on item size and made every create/disband contend on one
  write lock. A clan now hashes to `index-c{0..15}` and tags to `index-t{0..15}`; search and the
  clan leaderboard fan out over all shards concurrently, so the read still costs one round trip.
  Sharding is the only directory: no compatibility reads, no fallback, no migration layer. The
  pre-shard `index` item is simply abandoned — clans listed only there are no longer searchable and
  their tags are free for reuse. Their `clan-{id}` records and the owning players' `social.clanId`
  still exist in Cloud Save and must be cleared separately (new environment, or a Dashboard wipe);
  Cloud Save private custom data has no list-all API, so no script can enumerate them.
  Investigated and rejected the previously documented plan of "a Cloud Save index with a
  dashboard-configured query": Cloud Save query indexes only cover *player* data, and clan records
  live in *private custom* data precisely so player tokens cannot reach them. Using them would
  have meant giving up the security model.
  **This reasoning was wrong and is corrected in 1.0.0.** Cloud Save queries cover Game Data as
  well as Player Data, and from Cloud Code they cover any access class including Private. The
  security model never had to be given up; the shards existed only because of a claim nobody
  checked against the documentation. The sharded directory described above was replaced by a real
  query index.
- Disband leaderboard deletion now tries two authorised server-side identities before degrading:
  `context.serviceToken` (Bearer), then the service account key pair sent as **Basic** auth
  (`UGS_SA_KEY_ID` / `UGS_SA_SECRET_KEY` from Secret Manager). Only if both are unauthorised does it
  fall back to zeroing. The response gained `leaderboardRemovalMethod` (`serviceToken` /
  `serviceAccount` / `zeroed` / `none`) so the fallback is no longer indistinguishable from a real
  delete.
  Verified live: `serviceTokenStatus 401` → `serviceAccountStatus 200`, `leaderboardRemovalMethod:
  "serviceAccount"`, entry genuinely gone from the board.
  The first implementation exchanged the key pair for a Bearer token at
  `/auth/v1/token-exchange` first. That is wrong and returned **401**: the exchanged token is a
  project/player credential, not an admin one. UGS admin APIs authenticate a service account with
  the key pair sent directly as Basic — the same `Authorization: Basic …` header the Dashboard
  shows when a key is created.

### Fixed
- `deletePrivateCustomItem` was called as `(key, projectId, customId)`; the cloud-save-1.4 signature
  is `(projectId, customId, key)`. It sits inside a `try/catch`, so disband had been silently
  leaving every `clan-*` item behind.
- `.header` and `.tabbar` defaulted to `flex-shrink: 1`, so a tall page squeezed them vertically -
  the header rendered 47px tall while its content needed 72px, clipping the player name and pushing
  the meta line into the tab bar. Both are now fixed chrome; `.content` is the row that yields.
- `.header-identity` gained `min-width: 0` so it can shrink below its content at all, with ellipsis
  on both labels instead of painting over the rename field.
- Sign-in showed a frozen screen for its whole duration. It now shows a spinner and names the stage
  it is waiting on, because the slow stage is not always the same one.
- The clan search Play Mode test retries for 3s: a query index updates a moment after the write
  that feeds it, so asserting on the first attempt was a race the test would lose at random.
- **Chat page elements overlapped each other.** The `GLOBAL` / `CLAN` tabs painted on top of the
  voice participants label, and the tabs bled into one another. Nothing was mispositioned: every
  row in the chat column had the default `flex-shrink: 1`, so `.voice-people` was squeezed to 90px
  while its children needed ~134px. UI Toolkit does not clip overflow, so the overflowing children
  kept drawing straight over the row below. Measured before the fix - `voice-people` ended at
  `y=248.3` while its label ran to `y=290.8` and `.chat-head` started at `y=256.7`.
  The fixed chrome rows (`.voice-bar`, `.voice-people`, `.chat-head`, `.chat-input-row`) and the
  `.subtab` buttons are now `flex-shrink: 0`, and `.chat-list` is the single row that yields, its
  minimum lowered from 260px to 120px so it absorbs a short page instead of pushing the chrome off
  it. Verified by measuring world bounds at both full height and a 320px page: no overflow, no
  overlap, and `.chat-list` correctly collapsing to its 120px floor.
- **A renamed player kept sending chat under the old name.** Vivox stamps the sender name into each
  message from the *login session* and exposes no setter for it, so `LoginOptions.DisplayName` -
  set once at sign-in - was the real source of truth, not the name service. Renaming updated UGS
  Authentication and the clan roster while the chat session kept the original name indefinitely.
  (The gateway's `PlayerNameChanged` event was also raised but never subscribed to.)
  `ICommunicationService.UpdateDisplayNameAsync` now applies a rename by rebuilding the session
  under the new name and restoring channel membership, active voice channel, and mic/speaker state;
  `SocialCoordinator.SetPlayerNameAsync` awaits it as part of the rename. Verified live on both
  Global and Clan chat: messages sent before the rename keep the old name, messages after carry the
  new one, and the session stays connected. Covered by a new Play Mode test,
  `Rename_KeepsChatSessionAliveUnderNewName`.
- The shard hash used `hash * 0x01000193`, a float64 multiply that loses precision past 2^53 and
  corrupts the result — measured **15x** bucket skew on realistic 4-character tags, with empty
  buckets. Switched to `Math.imul` and added a MurmurHash3 finalizer, because `% 16` keeps only the
  low four bits and FNV-1a's low bits are its weakest. Measured spread is now 1.14x on tags and
  1.10x on clan ids, verified against the shipped source rather than a retyped copy.
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
- Cloud Code deployment can report `Failed to Deploy` with a "was found duplicated with other
  files" message. This is a bug in `com.unity.services.cloudcode` 2.10.4: `Script.Name` is null
  after a domain reload, and `PreDeployValidator` groups the batch by name, so two null names look
  like the same script. Reimport the `CloudCode` folder before deploying, or deploy one script at
  a time.
- No migration tooling, deliberately. Cloud Save has no list-all API for custom items, so nothing
  inside the backend can enumerate every clan or player - a migration would have to walk the index
  it is replacing and would still miss anyone not on a clan roster. Wiping the environment is the
  supported path for a layout change.
- Three Cloud Save indexes must exist before the first clan is written; only data written after an
  index exists is queryable. Config in `docs/cloud-save-indexes.json`.
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
