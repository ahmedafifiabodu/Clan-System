const { DataApi } = require("@unity-services/cloud-save-1.4");
const { LeaderboardsApi } = require("@unity-services/leaderboards-1.1");

// The only path that may write a score. Clients submit a *match result*, never a total:
// the server clamps the delta, rate-limits submissions, and derives both the player total
// and the clan total from data the client cannot touch.

const CONFIG = {
  playerLeaderboardId: "player_score",
  clanLeaderboardId: "clan_score",
  maxScoreDelta: 500,
  minSubmitIntervalMs: 2000,
  pageLimitMax: 100,
  clanXpPerPoint: 1,
};

const ok = (data) => ({ ok: true, code: "OK", message: "", data: data || {} });
const fail = (code, message) => ({ ok: false, code: code, message: message, data: {} });

const clanId_ = (id) => `clan-${id}`;
const playerId_ = (id) => `player-${id}`;

async function readPrivate(api, projectId, customId, key) {
  const res = await api.getPrivateCustomItems(projectId, customId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return { value: null, writeLock: null };
  return { value: results[0].value, writeLock: results[0].writeLock };
}

async function writePrivate(api, projectId, customId, key, value, writeLock) {
  const body = { key: key, value: value };
  if (writeLock) body.writeLock = writeLock;
  await api.setPrivateCustomItem(projectId, customId, body);
}

async function mutate(api, projectId, customId, key, mutator) {
  let lastError = null;
  for (let attempt = 0; attempt < 4; attempt++) {
    const current = await readPrivate(api, projectId, customId, key);
    const next = await mutator(current.value);
    if (next && next.__abort) return next;
    try {
      await writePrivate(api, projectId, customId, key, next, current.writeLock);
      return next;
    } catch (err) {
      const status = err && err.response && err.response.status;
      if (status !== 409) throw err;
      lastError = err;
    }
  }
  throw lastError || new Error("write conflict");
}

function levelFor(xp) {
  return Math.max(1, Math.floor(Math.sqrt(Math.max(0, xp) / 250)) + 1);
}

function mapEntries(results) {
  const rows = [];
  const list = results || [];
  for (let i = 0; i < list.length; i++) {
    const entry = list[i];
    rows.push({
      rank: entry.rank + 1,
      id: entry.playerId,
      name: entry.playerName || null,
      score: entry.score,
    });
  }
  return rows;
}

module.exports = async ({ params, context, logger }) => {
  const api = new DataApi(context);
  const leaderboards = new LeaderboardsApi(context);
  const projectId = context.projectId;
  const callerId = context.playerId;
  const payload = params.payload || {};
  const now = Date.now();

  if (!callerId) return fail("UNAUTHENTICATED", "No player context.");

  const clanTagCache = {};
  async function clanTagOf(clanId) {
    if (clanTagCache[clanId] !== undefined) return clanTagCache[clanId];
    const item = await readPrivate(api, projectId, clanId_(clanId), "profile");
    clanTagCache[clanId] = item.value ? item.value.tag : null;
    return clanTagCache[clanId];
  }

  switch (params.action) {
    case "submitScore": {
      const requested = Number(payload.delta);
      if (!isFinite(requested) || requested <= 0) return fail("INVALID_SCORE", "Score delta must be positive.");
      const delta = Math.min(CONFIG.maxScoreDelta, Math.floor(requested));

      const rateItem = await readPrivate(api, projectId, playerId_(callerId), "rate");
      const rate = rateItem.value || {};
      if (rate.lastScoreAt && now - rate.lastScoreAt < CONFIG.minSubmitIntervalMs) {
        return fail("RATE_LIMITED", "Score submitted too frequently.");
      }

      const social = await mutate(api, projectId, playerId_(callerId), "social", (current) => {
        const value = current || { playerId: callerId, clanId: null, role: null, score: 0, contribution: 0 };
        value.score = (value.score || 0) + delta;
        value.contribution = (value.contribution || 0) + delta;
        value.lastActive = now;
        return value;
      });

      rate.lastScoreAt = now;
      await writePrivate(api, projectId, playerId_(callerId), "rate", rate, rateItem.writeLock);

      let playerEntry = null;
      try {
        const res = await leaderboards.addLeaderboardPlayerScore(projectId, CONFIG.playerLeaderboardId, callerId, { score: social.score });
        playerEntry = res && res.data ? res.data : null;
      } catch (err) {
        return fail("LEADERBOARD_UNAVAILABLE", "Score saved, but the leaderboard is unavailable.");
      }

      let clanProfile = null;
      if (social.clanId) {
        const members = await mutate(api, projectId, clanId_(social.clanId), "members", (current) => {
          const map = (current && current.map) || {};
          if (map[callerId]) {
            map[callerId].contribution = (map[callerId].contribution || 0) + delta;
            map[callerId].lastActive = now;
          }
          return { map: map };
        });

        // The clan total is derived from the roster rather than incremented, so a dropped
        // update or a retried write can never leave the aggregate drifting from its members.
        let total = 0;
        const memberMap = (members && members.map) || {};
        for (const memberId of Object.keys(memberMap)) {
          total += memberMap[memberId].contribution || 0;
        }

        clanProfile = await mutate(api, projectId, clanId_(social.clanId), "profile", (current) => {
          if (!current) return { __abort: true };
          current.score = total;
          current.xp = (current.xp || 0) + delta * CONFIG.clanXpPerPoint;
          current.level = levelFor(current.xp);
          return current;
        });

        if (clanProfile && !clanProfile.__abort) {
          try {
            await leaderboards.addLeaderboardPlayerScore(projectId, CONFIG.clanLeaderboardId, clanProfile.clanId, {
              score: clanProfile.score,
              metadata: {
                name: clanProfile.name,
                tag: clanProfile.tag,
                memberCount: clanProfile.memberCount,
                level: clanProfile.level,
              },
            });
          } catch (err) {
            // Player score already persisted; clan leaderboard will re-sync on the next submit.
          }
        }
      }

      return ok({
        playerScore: social.score,
        delta: delta,
        clanScore: clanProfile && !clanProfile.__abort ? clanProfile.score : 0,
        clanLevel: clanProfile && !clanProfile.__abort ? clanProfile.level : 0,
      });
    }

    case "players": {
      const limit = Math.min(CONFIG.pageLimitMax, Math.max(1, payload.limit || 25));
      const offset = Math.max(0, payload.offset || 0);
      try {
        const res = await leaderboards.getLeaderboardScores(projectId, CONFIG.playerLeaderboardId, offset, limit);
        const body = (res && res.data) || {};
        const rows = mapEntries(body.results);

        // The leaderboard stores ids and scores; clan affiliation lives in our own records,
        // so it is joined here rather than trusted from a client-supplied payload.
        for (let i = 0; i < rows.length; i++) {
          const item = await readPrivate(api, projectId, playerId_(rows[i].id), "social");
          const value = item.value || {};
          if (!rows[i].name) rows[i].name = value.name || null;
          rows[i].clanTag = value.clanId ? await clanTagOf(value.clanId) : null;
          rows[i].isSelf = rows[i].id === callerId;
        }

        let self = null;
        try {
          const mine = await leaderboards.getLeaderboardPlayerScore(projectId, CONFIG.playerLeaderboardId, callerId);
          if (mine && mine.data) {
            self = { rank: mine.data.rank + 1, id: mine.data.playerId, name: mine.data.playerName || null, score: mine.data.score, isSelf: true };
          }
        } catch (err) {
          self = null;
        }
        return ok({ rows: rows, total: body.total || 0, offset: offset, limit: limit, self: self });
      } catch (err) {
        return fail("LEADERBOARD_UNAVAILABLE", "The player leaderboard is not configured yet.");
      }
    }

    case "clans": {
      const limit = Math.min(CONFIG.pageLimitMax, Math.max(1, payload.limit || 25));
      const offset = Math.max(0, payload.offset || 0);
      const socialItem = await readPrivate(api, projectId, playerId_(callerId), "social");
      const myClanId = (socialItem.value && socialItem.value.clanId) || null;
      const indexItem = await readPrivate(api, projectId, "index", "clans");
      const index = (indexItem.value && indexItem.value.map) || {};

      try {
        const res = await leaderboards.getLeaderboardScores(projectId, CONFIG.clanLeaderboardId, offset, limit);
        const body = (res && res.data) || {};
        const rows = [];
        const entries = mapEntries(body.results);
        for (let i = 0; i < entries.length; i++) {
          const summary = index[entries[i].id];
          if (!summary) continue; // Disbanded clans keep a stale leaderboard entry; drop them.
          rows.push({
            rank: entries[i].rank,
            id: entries[i].id,
            name: summary.name,
            tag: summary.tag,
            score: entries[i].score,
            memberCount: summary.memberCount,
            maxMembers: summary.maxMembers,
            level: summary.level,
            isSelf: entries[i].id === myClanId,
          });
        }

        let self = null;
        if (myClanId) {
          try {
            const mine = await leaderboards.getLeaderboardPlayerScore(projectId, CONFIG.clanLeaderboardId, myClanId);
            const summary = index[myClanId];
            if (mine && mine.data && summary) {
              self = {
                rank: mine.data.rank + 1,
                id: myClanId,
                name: summary.name,
                tag: summary.tag,
                score: mine.data.score,
                memberCount: summary.memberCount,
                maxMembers: summary.maxMembers,
                level: summary.level,
                isSelf: true,
              };
            }
          } catch (err) {
            self = null;
          }
        }
        return ok({ rows: rows, total: body.total || 0, offset: offset, limit: limit, self: self, myClanId: myClanId });
      } catch (err) {
        return fail("LEADERBOARD_UNAVAILABLE", "The clan leaderboard is not configured yet.");
      }
    }

    default:
      return fail("UNKNOWN_ACTION", `Unsupported action '${params.action}'.`);
  }
};

module.exports.params = { action: "String", payload: "JSON" };

// schema-rev 2
