const { DataApi } = require("@unity-services/cloud-save-1.4");
const { PlayerNamesApi } = require("@unity-services/player-names-1.0");

// Read-side of the clan system. Membership is re-checked here rather than trusted from
// the request, so a tampered client cannot read a clan roster or log it does not belong to.

const CONFIG = {
  searchLimitMax: 50,
  directoryLimitMax: 50,
  activityLimitMax: 50,
  playersInfoMax: 50,
};

const ROLE_RANK = { Owner: 3, Officer: 2, Member: 1 };

const ok = (data) => ({ ok: true, code: "OK", message: "", data: data || {} });
const fail = (code, message) => ({ ok: false, code: code, message: message, data: {} });

const clanId_ = (id) => `clan-${id}`;
const playerId_ = (id) => `player-${id}`;
const INDEX_ID = "index";

async function readPrivate(api, projectId, customId, key) {
  const res = await api.getPrivateCustomItems(projectId, customId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return { value: null, writeLock: null };
  return { value: results[0].value, writeLock: results[0].writeLock };
}

function publicView(profile) {
  if (!profile) return null;
  return {
    clanId: profile.clanId,
    name: profile.name,
    tag: profile.tag,
    description: profile.description,
    motd: profile.motd,
    ownerId: profile.ownerId,
    ownerName: profile.ownerName,
    createdAt: profile.createdAt,
    memberCount: profile.memberCount,
    maxMembers: profile.maxMembers,
    isPublic: profile.isPublic,
    emblemId: profile.emblemId,
    score: profile.score,
    xp: profile.xp,
    level: profile.level,
  };
}

function sortMembers(map) {
  const list = [];
  for (const key of Object.keys(map)) list.push(map[key]);
  list.sort((a, b) => {
    const rank = (ROLE_RANK[b.role] || 0) - (ROLE_RANK[a.role] || 0);
    if (rank !== 0) return rank;
    return (b.contribution || 0) - (a.contribution || 0);
  });
  return list;
}

module.exports = async ({ params, context, logger }) => {
  const api = new DataApi(context);
  const projectId = context.projectId;
  const callerId = context.playerId;
  const payload = params.payload || {};
  const now = Date.now();

  if (!callerId) return fail("UNAUTHENTICATED", "No player context.");

  const socialItem = await readPrivate(api, projectId, playerId_(callerId), "social");
  const social = socialItem.value || { playerId: callerId, clanId: null, role: null, score: 0, contribution: 0 };

  const loadProfile = async (clanId) => {
    const item = await readPrivate(api, projectId, clanId_(clanId), "profile");
    return item.value;
  };

  switch (params.action) {
    case "me": {
      let clan = null;
      let members = [];
      if (social.clanId) {
        clan = await loadProfile(social.clanId);
        if (!clan) {
          social.clanId = null;
          social.role = null;
        } else {
          const membersItem = await readPrivate(api, projectId, clanId_(social.clanId), "members");
          members = sortMembers((membersItem.value && membersItem.value.map) || {});
        }
      }

      const invitesItem = await readPrivate(api, projectId, playerId_(callerId), "invites");
      const inviteMap = (invitesItem.value && invitesItem.value.map) || {};
      const invites = [];
      for (const key of Object.keys(inviteMap)) {
        if (inviteMap[key].expiresAt > now) invites.push(inviteMap[key]);
      }
      invites.sort((a, b) => b.createdAt - a.createdAt);

      let joinRequests = [];
      if (clan && ROLE_RANK[social.role] >= ROLE_RANK.Officer) {
        const requestsItem = await readPrivate(api, projectId, clanId_(social.clanId), "requests");
        const requestMap = (requestsItem.value && requestsItem.value.map) || {};
        for (const key of Object.keys(requestMap)) joinRequests.push(requestMap[key]);
      }

      return ok({
        profile: {
          playerId: callerId,
          name: social.name || null,
          clanId: social.clanId,
          role: social.role,
          score: social.score || 0,
          contribution: social.contribution || 0,
          joinedAt: social.joinedAt || 0,
        },
        clan: publicView(clan),
        members: members,
        invites: invites,
        joinRequests: joinRequests,
      });
    }

    case "getClan": {
      const clan = await loadProfile(payload.clanId);
      if (!clan) return fail("CLAN_NOT_FOUND", "That clan no longer exists.");
      const isMember = social.clanId === clan.clanId;
      let members = [];
      if (isMember) {
        const membersItem = await readPrivate(api, projectId, clanId_(clan.clanId), "members");
        members = sortMembers((membersItem.value && membersItem.value.map) || {});
      }
      return ok({ clan: publicView(clan), members: members, isMember: isMember });
    }

    case "members": {
      if (!social.clanId) return fail("NOT_IN_CLAN", "You are not in a clan.");
      const membersItem = await readPrivate(api, projectId, clanId_(social.clanId), "members");
      return ok({ members: sortMembers((membersItem.value && membersItem.value.map) || {}) });
    }

    case "search": {
      const indexItem = await readPrivate(api, projectId, INDEX_ID, "clans");
      const map = (indexItem.value && indexItem.value.map) || {};
      const query = typeof payload.query === "string" ? payload.query.trim().toUpperCase() : "";
      const onlyPublic = payload.onlyPublic !== false;
      const limit = Math.min(CONFIG.searchLimitMax, Math.max(1, payload.limit || 20));
      const offset = Math.max(0, payload.offset || 0);

      const matches = [];
      for (const key of Object.keys(map)) {
        const summary = map[key];
        if (onlyPublic && summary.isPublic === false) continue;
        if (query.length > 0) {
          const nameUpper = summary.nameUpper || (summary.name || "").toUpperCase();
          if (nameUpper.indexOf(query) < 0 && (summary.tag || "").indexOf(query) < 0) continue;
        }
        matches.push(summary);
      }
      matches.sort((a, b) => (b.score || 0) - (a.score || 0));

      return ok({ clans: matches.slice(offset, offset + limit), total: matches.length, offset: offset, limit: limit });
    }

    case "activity": {
      if (!social.clanId) return fail("NOT_IN_CLAN", "You are not in a clan.");
      const activityItem = await readPrivate(api, projectId, clanId_(social.clanId), "activity");
      const entries = (activityItem.value && activityItem.value.entries) || [];
      const limit = Math.min(CONFIG.activityLimitMax, Math.max(1, payload.limit || 25));
      return ok({ entries: entries.slice(0, limit) });
    }

    case "joinRequests": {
      if (!social.clanId) return fail("NOT_IN_CLAN", "You are not in a clan.");
      if (ROLE_RANK[social.role] < ROLE_RANK.Officer) return fail("PERMISSION_DENIED", "Requires Officer rank.");
      const requestsItem = await readPrivate(api, projectId, clanId_(social.clanId), "requests");
      const map = (requestsItem.value && requestsItem.value.map) || {};
      const list = [];
      for (const key of Object.keys(map)) list.push(map[key]);
      list.sort((a, b) => b.createdAt - a.createdAt);
      return ok({ requests: list });
    }

    case "playersInfo": {
      const ids = Array.isArray(payload.playerIds) ? payload.playerIds.slice(0, CONFIG.playersInfoMax) : [];
      const namesApi = new PlayerNamesApi(context);
      const players = [];
      for (const id of ids) {
        if (typeof id !== "string" || id.length === 0) continue;
        const item = await readPrivate(api, projectId, playerId_(id), "social");
        const value = item.value || {};
        let name = value.name || null;
        if (!name) {
          try {
            const res = await namesApi.getName(id);
            name = (res && res.data && res.data.name) || null;
          } catch (err) {
            name = null;
          }
        }
        let clanName = null;
        let clanTag = null;
        if (value.clanId) {
          const clan = await loadProfile(value.clanId);
          if (clan) {
            clanName = clan.name;
            clanTag = clan.tag;
          }
        }
        players.push({
          playerId: id,
          name: name,
          clanId: value.clanId || null,
          clanName: clanName,
          clanTag: clanTag,
          role: value.role || null,
          score: value.score || 0,
          lastActive: value.lastActive || 0,
        });
      }
      return ok({ players: players });
    }

    default:
      return fail("UNKNOWN_ACTION", `Unsupported action '${params.action}'.`);
  }
};

module.exports.params = { action: "String", payload: "JSON" };

// schema-rev 2
