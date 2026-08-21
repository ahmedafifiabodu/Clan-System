# Cloud Save layout

What lives where, why, and how to set up an environment from scratch.

## Access classes, and the one that was missing

Cloud Save offers five places to put data. Three of them matter here:

| Storage | Player can read | Player can write | Scoped to |
| --- | --- | --- | --- |
| Player Data — **Default** | yes | **yes** | player |
| Player Data — **Public** | anyone | **yes** | player |
| Player Data — **Protected** | yes | no | player |
| Game Data — **Default** | yes | no | game |
| Game Data — **Private** | no | no | game |

The clan system needs two guarantees, and they are not the same guarantee:

- **Clan records** must be unreachable with a player token. A client must not be able to read
  another clan's roster even by forging requests. Only *Private* Game Data gives that.
- **Per-player records** must be unwritable by the player who owns them. Reading them is fine —
  they are that player's own clan, invites, cooldowns and mute, all of which they already know.
  *Protected* Player Data gives exactly that.

Every per-player record used to sit in Private Game Data as a `player-<id>` custom item. That was
one access class too strict, and — more importantly — the wrong **scope**: a custom item belongs to
the game, so it is not removed when the player is deleted. Every one of them was an orphan waiting
to happen, and nothing could enumerate them to clean up. They are now Protected Player Data, which
UGS removes along with the player.

## Current layout

### Private Game Data (custom items)

| Item | Keys | Purpose |
| --- | --- | --- |
| `clan-<clanId>` | `profile`, `members`, `activity`, `requests`, `chat` | The clan itself |
| `clan-<clanId>` | `summary`, `dirPublic`, `dirScore`, `dirTag`, `dirNameUpper` | Directory facets — see below |
| `index-t<0..15>` | `tags` | Tag uniqueness reservation |

### Protected Player Data

| Key | Purpose |
| --- | --- |
| `social` | Clan membership, role, score, contribution |
| `invites` | Pending clan invitations addressed to this player |
| `rate` | Per-player cooldowns (clan create, score submit) |
| `moderation` | Text/voice/game restrictions written by the moderation scripts |

### Why the tag shards stayed

`index-t<0..15>` looks like the clan directory shards that were removed, and is not the same thing.
The directory was a *list* — something to read — and a query index replaces a list perfectly. Tag
uniqueness is a *reservation*: two clans racing for `[WOLF]` must be serialised, and the write lock
on the shard item is what does that. A query is a read, and a read cannot reserve anything.

The hash that picks the shard is FNV-1a with a MurmurHash3 finalizer, and it uses `Math.imul`.
Plain `hash * 0x01000193` is a float64 multiply that silently loses precision past 2^53; measured
against realistic tags it produced 15x bucket skew with several buckets empty. `Math.imul` plus the
finalizer holds spread under 1.2x.

## The directory index

Browse and search query the `clan-<id>` items directly. There is no separate directory item to
keep in step with the profiles, which removes a whole class of drift.

Two constraints shape the facet keys:

- An indexed value is **at most 128 bytes**. Anything larger still saves, it just is not indexed.
  So the render-ready `summary` blob cannot be a filter — it is fetched via `returnKeys` instead.
- The whole project gets **20 indexed keys**, across every index, access class and entity type.
  The five below spend five of them.

### Indexes to create

Dashboard → Cloud Save → **Configure Indexes**. All are **Game Data / Private**.

| Index | Keys (in order) | Serves |
| --- | --- | --- |
| `clan_browse` | `dirPublic` asc, `dirScore` desc | Browse, ordered by score |
| `clan_by_name` | `dirPublic` asc, `dirNameUpper` asc | Name prefix search |
| `clan_by_tag` | `dirTag` asc | Exact tag lookup |

Or with the UGS CLI:

```bash
ugs cloud-save index create --file docs/cloud-save-indexes.json --project-id <id> --environment-name <env>
```

> **Only data written after an index is created is queryable.** A clan written before its index
> existed is invisible to browse until something rewrites it, and nothing does. Create the indexes
> before the first clan.

### Search is a prefix match, not a substring match

Cloud Save queries compare (`EQ`, `NE`, `LT`, `LE`, `GT`, `GE`) — they do not search. A name filter
is therefore `dirNameUpper GE "WOL"` bounded above by `"WOL￿"`, which matches names *starting
with* `WOL`. Typing `OLF` no longer finds `WOLFPACK`.

The old fan-out did substring matching because it had already loaded every clan into memory, which
is the thing that did not scale. Exact tag match still works on any tag, and is ranked above name
hits, so the fastest way to find a specific clan is still to type its tag.

## Setting up an environment

1. Create the three indexes above.
2. Deploy the Cloud Code scripts in `Assets/ClanSystem/CloudCode/`.

   If the Deployment window reports `Failed to Deploy` with a "was found duplicated with other
   files" message, right-click the `CloudCode` folder and Reimport first. The Cloud Code authoring
   package (2.10.4) leaves `Script.Name` null after a domain reload, and its pre-deploy validator
   groups the batch by name - two null names look like the same script. Reimporting repopulates the
   names from their paths. Deploying one script at a time also works, for the same reason.
3. Create the `player_score` and `clan_score` leaderboards.
4. Set the `VIVOX_ISSUER` and `VIVOX_KEY` secrets.

Order matters: **only data written after an index exists is queryable**, so a clan created before
step 1 never appears in browse.

### Changing this layout later

There is no migration tooling, deliberately. Cloud Save has no list-all API for custom items, so
nothing running inside the backend can enumerate every clan or player — a migration would have to
walk an index it is trying to replace, and would still miss any player not on a clan roster.

Wiping the environment is the supported path. From the Dashboard, delete everything under Game Data
and Player Data, then follow the four steps above.

That is a real cost, not a free reset: it destroys every clan, roster, invite and score, and it
clears active moderation records, so anyone currently muted comes back unmuted. Worth checking the
moderation list before wiping a live environment.
