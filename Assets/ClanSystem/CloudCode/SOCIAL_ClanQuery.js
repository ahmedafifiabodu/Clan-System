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

const DIRECTORY_KEYS = ["summary"];

async function readPrivate(api, projectId, customId, key) {
  const res = await api.getPrivateCustomItems(projectId, customId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return { value: null, writeLock: null };
  return { value: results[0].value, writeLock: results[0].writeLock };
}

// Per-player state is protected player data: the player reads it, only the server writes it.
async function readPlayer(api, projectId, playerId, key) {
  const res = await api.getProtectedItems(projectId, playerId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return { value: null, writeLock: null };
  return { value: results[0].value, writeLock: results[0].writeLock };
}


/// Turns a Cloud Save failure into something a player-facing message can carry.
///
/// The service errors arrive as axios rejections, and an axios rejection thrown out of a Cloud Code
/// script is reported to the client as `problems/invocation/axios` - a discriminator the client SDK
/// does not know, so the whole failure degrades to "Unknown" with no cause attached. Reading the
/// status and body here is what keeps a missing index distinguishable from an outage.
function describeApiError(err) {
  const response = err && err.response;
  if (!response) return err && err.message ? err.message : "Unknown error.";

  let body = response.data;
  if (body && typeof body === "object") {
    body = body.detail || body.title || body.message || JSON.stringify(body);
  }

  return `HTTP ${response.status}${body ? `: ${body}` : ""}`;
}

/// Runs one directory query and returns the clan summaries it matched.
///
/// `fields` is ANDed by the service and also decides the sort, so every read path here states its
/// ordering as part of its filter rather than sorting the page afterwards - sorting after the fact
/// would only order the slice that came back, which is not the same thing.
///
/// The summary blob is requested through `returnKeys`. A row that comes back without one is read
/// directly instead - `returnKeys` is documented as a projection but not documented to cover keys
/// the index does not itself carry, so the fallback is what makes browse correct either way. If it
/// fires on every row, the symptom is a slow browse rather than a wrong one.
async function queryDirectory(api, projectId, label, fields, limit, offset) {
  let res;
  try {
    res = await api.queryPrivateCustomData(projectId, {
      fields: fields,
      returnKeys: DIRECTORY_KEYS,
      offset: offset,
      limit: limit,
    });
  } catch (err) {
    // Named after the index it needs, because the usual cause is that index not existing in this
    // environment - and the query alone does not say which one it was.
    const wrapped = new Error(`Directory query '${label}' failed - ${describeApiError(err)}`);
    wrapped.isDirectoryQueryError = true;
    throw wrapped;
  }

  const results = (res && res.data && res.data.results) || [];
  const pending = [];
  for (let i = 0; i < results.length; i++) {
    const row = results[i];
    const entries = row.data || [];
    let summary = null;
    for (let e = 0; e < entries.length; e++) {
      if (entries[e].key === "summary") summary = entries[e].value;
    }
    if (summary) {
      pending.push(Promise.resolve(summary));
    } else if (row.id) {
      pending.push(readPrivate(api, projectId, row.id, "summary").then((item) => item.value));
    }
  }

  const summaries = await Promise.all(pending);
  const list = [];
  for (let i = 0; i < summaries.length; i++) {
    if (summaries[i]) list.push(summaries[i]);
  }
  return list;
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

  const socialItem = await readPlayer(api, projectId, callerId, "social");
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

      const invitesItem = await readPlayer(api, projectId, callerId, "invites");
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
      const query = typeof payload.query === "string" ? payload.query.trim().toUpperCase() : "";
      const onlyPublic = payload.onlyPublic !== false;
      const limit = Math.min(CONFIG.searchLimitMax, Math.max(1, payload.limit || 20));
      const offset = Math.max(0, payload.offset || 0);

      // Public-ness is an equality facet so it can sit in front of the ordered facet in a compound
      // index. When the caller wants private clans too the filter is dropped entirely rather than
      // widened, because `dirPublic NE 0` would need an index the browse path does not have.
      const visibility = onlyPublic ? [{ key: "dirPublic", op: "EQ", value: 1, asc: true }] : [];

      let matches;
      try {
        if (query.length === 0) {
          // Browse: no term, so the whole index is the result set, ordered by score.
          // `dirScore GE 0` is a filter that excludes nothing - scores are clamped non-negative -
          // and exists only to name the key the service should sort on.
          matches = await queryDirectory(api, projectId, "clan_browse",
            visibility.concat([{ key: "dirScore", op: "GE", value: 0, asc: false }]), limit, offset);
        } else {
          // A term is matched two ways, and the two cannot share a request: `fields` is ANDed, and a
          // clan matches on its tag *or* its name. They run concurrently and are merged below.
          //
          // Name matching is a prefix, not a substring. Cloud Save queries compare, they do not
          // search, so `GE term` with an upper bound just past the term is the strongest name filter
          // an index can serve. `startsWith` is re-checked locally because the bound is only correct
          // if the service compares exactly as JavaScript does.
          //
          // The tag lookup deliberately drops the visibility facet. A query is served by the index
          // whose keys it names, and `clan_by_tag` is keyed on `dirTag` alone - adding `dirPublic`
          // to the filter asks for a compound index that does not exist, and the service rejects
          // the whole request. Tags are unique, so the hit is filtered for public-ness below
          // instead, which costs one comparison rather than an index slot.
          const upperBound = query + "￿";
          const [byTag, byName] = await Promise.all([
            queryDirectory(api, projectId, "clan_by_tag",
              [{ key: "dirTag", op: "EQ", value: query, asc: true }], limit, 0),
            queryDirectory(api, projectId, "clan_by_name",
              visibility.concat([
                { key: "dirNameUpper", op: "GE", value: query, asc: true },
                { key: "dirNameUpper", op: "LT", value: upperBound, asc: true },
              ]), limit + offset, 0),
          ]);

          const byScore = (a, b) => (b.score || 0) - (a.score || 0);
          const seen = {};
          const tagHits = [];
          const nameHits = [];

          for (let i = 0; i < byTag.length; i++) {
            // Stands in for the `dirPublic` filter the tag index cannot carry.
            if (onlyPublic && byTag[i].isPublic === false) continue;
            if (seen[byTag[i].clanId]) continue;
            seen[byTag[i].clanId] = true;
            tagHits.push(byTag[i]);
          }
          for (let i = 0; i < byName.length; i++) {
            const summary = byName[i];
            const nameUpper = summary.nameUpper || (summary.name || "").toUpperCase();
            if (nameUpper.indexOf(query) !== 0) continue;
            if (seen[summary.clanId]) continue;
            seen[summary.clanId] = true;
            nameHits.push(summary);
          }

          // An exact tag hit is the strongest signal a player can give, so the whole tag group
          // outranks the whole name group. Sorting the merged list by score instead would let a
          // high-scoring name match jump ahead of the clan whose tag was typed verbatim.
          tagHits.sort(byScore);
          nameHits.sort(byScore);
          matches = tagHits.concat(nameHits).slice(offset, offset + limit);
        }
      } catch (err) {
        if (!err || !err.isDirectoryQueryError) throw err;
        if (logger) logger.error(err.message);
        return fail("SERVICE_UNAVAILABLE", err.message);
      }

      // The service does not report how many rows a query could have returned, so `total` is what
      // the caller has actually been shown. `hasMore` is the honest signal for paging.
      return ok({
        clans: matches,
        total: offset + matches.length,
        offset: offset,
        limit: limit,
        hasMore: matches.length >= limit,
      });
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
        const item = await readPlayer(api, projectId, id, "social");
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

// schema-rev 4
