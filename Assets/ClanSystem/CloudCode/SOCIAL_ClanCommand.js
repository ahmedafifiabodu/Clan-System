const { DataApi } = require("@unity-services/cloud-save-1.4");
const { LeaderboardsApi } = require("@unity-services/leaderboards-1.1");
const { PlayerNamesApi } = require("@unity-services/player-names-1.0");

// Server-authoritative clan mutations. The client never writes clan state directly:
// every field below lives in Cloud Save *private* custom data, which player tokens cannot reach.

const CONFIG = {
  maxMembersDefault: 30,
  maxMembersCeiling: 50,
  nameMin: 3,
  nameMax: 24,
  tagMin: 2,
  tagMax: 5,
  descriptionMax: 200,
  motdMax: 200,
  activityLogMax: 50,
  clanCreateCooldownMs: 60 * 1000,
  inviteTtlMs: 7 * 24 * 60 * 60 * 1000,
  maxPendingInvitesPerPlayer: 20,
  clanLeaderboardId: "clan_score",
  writeRetries: 4,
};

const ROLE = { owner: "Owner", officer: "Officer", member: "Member" };
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

async function writePrivate(api, projectId, customId, key, value, writeLock) {
  const body = { key: key, value: value };
  if (writeLock) body.writeLock = writeLock;
  await api.setPrivateCustomItem(projectId, customId, body);
}

// Optimistic concurrency: read -> mutate -> conditional write. A 409 means another
// player changed the same clan first, so we re-read and replay the mutation.
async function mutate(api, projectId, customId, key, mutator) {
  let lastError = null;
  for (let attempt = 0; attempt < CONFIG.writeRetries; attempt++) {
    const current = await readPrivate(api, projectId, customId, key);
    const next = await mutator(current.value);
    if (next === undefined) return null;
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

function sanitize(text, max) {
  if (typeof text !== "string") return "";
  return text.replace(/[\u0000-\u001F\u007F]/g, " ").replace(/\s+/g, " ").trim().slice(0, max);
}

function validateName(name) {
  const clean = sanitize(name, CONFIG.nameMax);
  if (clean.length < CONFIG.nameMin) return null;
  if (!/^[A-Za-z0-9 _'-]+$/.test(clean)) return null;
  return clean;
}

function validateTag(tag) {
  const clean = sanitize(tag, CONFIG.tagMax).toUpperCase().replace(/\s/g, "");
  if (clean.length < CONFIG.tagMin) return null;
  if (!/^[A-Z0-9]+$/.test(clean)) return null;
  return clean;
}

function newId(playerId, now) {
  return `${now.toString(36)}${playerId.slice(0, 6)}${Math.floor(Math.random() * 1e6).toString(36)}`;
}

function summarize(profile) {
  return {
    clanId: profile.clanId,
    name: profile.name,
    tag: profile.tag,
    memberCount: profile.memberCount,
    maxMembers: profile.maxMembers,
    score: profile.score,
    level: profile.level,
    emblemId: profile.emblemId,
    isPublic: profile.isPublic,
    nameUpper: profile.name.toUpperCase(),
  };
}

function levelFor(xp) {
  return Math.max(1, Math.floor(Math.sqrt(Math.max(0, xp) / 250)) + 1);
}

// Clan score is always derived from the roster, never adjusted by deltas, so membership
// changes and score submissions can never leave the total out of step with its members.
function sumContributions(memberMap) {
  let total = 0;
  const map = memberMap || {};
  for (const memberId of Object.keys(map)) {
    total += map[memberId].contribution || 0;
  }
  return total;
}

async function loadSocial(api, projectId, playerId) {
  const social = await readPrivate(api, projectId, playerId_(playerId), "social");
  return social.value || { playerId: playerId, clanId: null, role: null, score: 0, contribution: 0 };
}

async function resolveName(context, playerId, fallback) {
  try {
    const namesApi = new PlayerNamesApi(context);
    const res = await namesApi.getName(playerId);
    const name = res && res.data && res.data.name;
    if (name) return name;
  } catch (err) {
    // Name service is best-effort: a missing name must never block a clan operation.
  }
  return fallback || `Player-${playerId.slice(0, 6)}`;
}

async function appendActivity(api, projectId, clanId, entry) {
  await mutate(api, projectId, clanId_(clanId), "activity", (current) => {
    const list = (current && current.entries) || [];
    list.unshift(entry);
    return { entries: list.slice(0, CONFIG.activityLogMax) };
  });
}

async function syncClanLeaderboard(context, profile) {
  try {
    const leaderboards = new LeaderboardsApi(context);
    await leaderboards.addLeaderboardPlayerScore(context.projectId, CONFIG.clanLeaderboardId, profile.clanId, {
      score: profile.score,
      metadata: {
        name: profile.name,
        tag: profile.tag,
        memberCount: profile.memberCount,
        level: profile.level,
      },
    });
  } catch (err) {
    // A leaderboard outage must not roll back a successful clan mutation.
  }
}

async function updateIndex(api, projectId, clanId, summary) {
  await mutate(api, projectId, INDEX_ID, "clans", (current) => {
    const map = (current && current.map) || {};
    if (summary === null) delete map[clanId];
    else map[clanId] = summary;
    return { map: map };
  });
}

async function setPlayerSocial(api, projectId, playerId, social) {
  await mutate(api, projectId, playerId_(playerId), "social", () => social);
}

module.exports = async ({ params, context, logger }) => {
  const api = new DataApi(context);
  const projectId = context.projectId;
  const callerId = context.playerId;
  const payload = params.payload || {};
  const now = Date.now();

  if (!callerId) return fail("UNAUTHENTICATED", "No player context.");

  const social = await loadSocial(api, projectId, callerId);
  const callerName = await resolveName(context, callerId, social.name);
  if (social.name !== callerName) {
    social.name = callerName;
    await setPlayerSocial(api, projectId, callerId, social);
  }

  const loadClan = async (clanId) => {
    const profile = await readPrivate(api, projectId, clanId_(clanId), "profile");
    return profile.value;
  };

  const requireMembership = async (minRole) => {
    if (!social.clanId) return { error: fail("NOT_IN_CLAN", "You are not in a clan.") };
    const profile = await loadClan(social.clanId);
    if (!profile) {
      social.clanId = null;
      social.role = null;
      await setPlayerSocial(api, projectId, callerId, social);
      return { error: fail("CLAN_NOT_FOUND", "That clan no longer exists.") };
    }
    if (minRole && ROLE_RANK[social.role] < ROLE_RANK[minRole]) {
      return { error: fail("PERMISSION_DENIED", `Requires ${minRole} rank.`) };
    }
    return { profile: profile };
  };

  switch (params.action) {
    case "create": {
      if (social.clanId) return fail("ALREADY_IN_CLAN", "Leave your current clan first.");

      const rate = await readPrivate(api, projectId, playerId_(callerId), "rate");
      const rateValue = rate.value || {};
      if (rateValue.lastClanCreate && now - rateValue.lastClanCreate < CONFIG.clanCreateCooldownMs) {
        return fail("RATE_LIMITED", "You created a clan recently. Try again later.");
      }

      const name = validateName(payload.name);
      const tag = validateTag(payload.tag);
      if (!name) return fail("INVALID_NAME", `Clan name must be ${CONFIG.nameMin}-${CONFIG.nameMax} characters.`);
      if (!tag) return fail("INVALID_TAG", `Clan tag must be ${CONFIG.tagMin}-${CONFIG.tagMax} letters or digits.`);

      const tagsItem = await readPrivate(api, projectId, INDEX_ID, "tags");
      const tags = (tagsItem.value && tagsItem.value.map) || {};
      if (tags[tag]) return fail("TAG_TAKEN", `Tag [${tag}] is already in use.`);

      const clanId = newId(callerId, now);
      const profile = {
        clanId: clanId,
        name: name,
        tag: tag,
        description: sanitize(payload.description, CONFIG.descriptionMax),
        motd: "",
        ownerId: callerId,
        ownerName: callerName,
        createdAt: now,
        memberCount: 1,
        maxMembers: Math.min(CONFIG.maxMembersCeiling, payload.maxMembers || CONFIG.maxMembersDefault),
        isPublic: payload.isPublic !== false,
        emblemId: Math.max(0, Math.min(11, payload.emblemId | 0)),
        score: 0,
        xp: 0,
        level: 1,
      };

      const reserved = await mutate(api, projectId, INDEX_ID, "tags", (current) => {
        const map = (current && current.map) || {};
        if (map[tag]) return { __abort: true };
        map[tag] = clanId;
        return { map: map };
      });
      if (reserved && reserved.__abort) return fail("TAG_TAKEN", `Tag [${tag}] is already in use.`);

      await writePrivate(api, projectId, clanId_(clanId), "profile", profile, null);
      await writePrivate(api, projectId, clanId_(clanId), "members", {
        map: {
          [callerId]: {
            playerId: callerId,
            name: callerName,
            role: ROLE.owner,
            joinedAt: now,
            contribution: 0,
            lastActive: now,
          },
        },
      }, null);

      social.clanId = clanId;
      social.role = ROLE.owner;
      social.joinedAt = now;
      social.contribution = 0;
      await setPlayerSocial(api, projectId, callerId, social);
      await writePrivate(api, projectId, playerId_(callerId), "rate", Object.assign({}, rateValue, { lastClanCreate: now }), rate.writeLock);
      await updateIndex(api, projectId, clanId, summarize(profile));
      await appendActivity(api, projectId, clanId, { ts: now, type: "created", actorName: callerName, text: `${callerName} founded the clan.` });
      await syncClanLeaderboard(context, profile);

      return ok({ clan: profile, role: ROLE.owner });
    }

    case "join": {
      if (social.clanId) return fail("ALREADY_IN_CLAN", "Leave your current clan first.");
      const target = await loadClan(payload.clanId);
      if (!target) return fail("CLAN_NOT_FOUND", "That clan no longer exists.");
      if (!target.isPublic) return fail("CLAN_IS_PRIVATE", "This clan is invite-only. Send a join request instead.");
      if (target.memberCount >= target.maxMembers) return fail("CLAN_FULL", "That clan is full.");

      const result = await mutate(api, projectId, clanId_(target.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        if (Object.keys(map).length >= target.maxMembers) return { __abort: true };
        map[callerId] = {
          playerId: callerId,
          name: callerName,
          role: ROLE.member,
          joinedAt: now,
          contribution: 0,
          lastActive: now,
        };
        return { map: map };
      });
      if (result && result.__abort) return fail("CLAN_FULL", "That clan is full.");

      const updated = await mutate(api, projectId, clanId_(target.clanId), "profile", (current) => {
        const value = current || target;
        value.memberCount = Object.keys(result.map).length;
        return value;
      });

      social.clanId = target.clanId;
      social.role = ROLE.member;
      social.joinedAt = now;
      await setPlayerSocial(api, projectId, callerId, social);
      await updateIndex(api, projectId, target.clanId, summarize(updated));
      await appendActivity(api, projectId, target.clanId, { ts: now, type: "joined", actorName: callerName, text: `${callerName} joined the clan.` });

      return ok({ clan: updated, role: ROLE.member });
    }

    case "requestJoin": {
      if (social.clanId) return fail("ALREADY_IN_CLAN", "Leave your current clan first.");
      const target = await loadClan(payload.clanId);
      if (!target) return fail("CLAN_NOT_FOUND", "That clan no longer exists.");
      if (target.memberCount >= target.maxMembers) return fail("CLAN_FULL", "That clan is full.");

      await mutate(api, projectId, clanId_(target.clanId), "requests", (current) => {
        const map = (current && current.map) || {};
        map[callerId] = {
          playerId: callerId,
          name: callerName,
          message: sanitize(payload.message, 120),
          createdAt: now,
        };
        return { map: map };
      });
      return ok({ requested: true });
    }

    case "handleJoinRequest": {
      const gate = await requireMembership(ROLE.officer);
      if (gate.error) return gate.error;
      const targetId = payload.playerId;
      if (!targetId) return fail("INVALID_REQUEST", "Missing player.");

      let request = null;
      await mutate(api, projectId, clanId_(gate.profile.clanId), "requests", (current) => {
        const map = (current && current.map) || {};
        request = map[targetId] || null;
        delete map[targetId];
        return { map: map };
      });
      if (!request) return fail("REQUEST_NOT_FOUND", "That join request no longer exists.");
      if (!payload.accept) return ok({ handled: true, accepted: false });

      const targetSocial = await loadSocial(api, projectId, targetId);
      if (targetSocial.clanId) return fail("ALREADY_IN_CLAN", "That player already joined another clan.");

      const members = await mutate(api, projectId, clanId_(gate.profile.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        if (Object.keys(map).length >= gate.profile.maxMembers) return { __abort: true };
        map[targetId] = {
          playerId: targetId,
          name: request.name,
          role: ROLE.member,
          joinedAt: now,
          contribution: 0,
          lastActive: now,
        };
        return { map: map };
      });
      if (members && members.__abort) return fail("CLAN_FULL", "Your clan is full.");

      targetSocial.clanId = gate.profile.clanId;
      targetSocial.role = ROLE.member;
      targetSocial.joinedAt = now;
      await setPlayerSocial(api, projectId, targetId, targetSocial);

      const updated = await mutate(api, projectId, clanId_(gate.profile.clanId), "profile", (current) => {
        const value = current || gate.profile;
        value.memberCount = Object.keys(members.map).length;
        return value;
      });
      await updateIndex(api, projectId, updated.clanId, summarize(updated));
      await appendActivity(api, projectId, updated.clanId, { ts: now, type: "joined", actorName: request.name, text: `${request.name} was accepted by ${callerName}.` });
      return ok({ handled: true, accepted: true });
    }

    case "invite": {
      const gate = await requireMembership(ROLE.officer);
      if (gate.error) return gate.error;
      const targetId = payload.targetPlayerId;
      if (!targetId || targetId === callerId) return fail("INVALID_REQUEST", "Invalid invite target.");
      if (gate.profile.memberCount >= gate.profile.maxMembers) return fail("CLAN_FULL", "Your clan is full.");

      const targetSocial = await loadSocial(api, projectId, targetId);
      if (targetSocial.clanId === gate.profile.clanId) return fail("ALREADY_MEMBER", "That player is already in your clan.");

      const inviteId = newId(targetId, now);
      const stored = await mutate(api, projectId, playerId_(targetId), "invites", (current) => {
        const map = (current && current.map) || {};
        for (const key of Object.keys(map)) {
          if (map[key].expiresAt < now) delete map[key];
          else if (map[key].clanId === gate.profile.clanId) return { __abort: true };
        }
        if (Object.keys(map).length >= CONFIG.maxPendingInvitesPerPlayer) return { __abort: true };
        map[inviteId] = {
          inviteId: inviteId,
          clanId: gate.profile.clanId,
          clanName: gate.profile.name,
          clanTag: gate.profile.tag,
          senderId: callerId,
          senderName: callerName,
          receiverId: targetId,
          createdAt: now,
          expiresAt: now + CONFIG.inviteTtlMs,
          status: "Pending",
        };
        return { map: map };
      });
      if (stored && stored.__abort) return fail("INVITE_EXISTS", "That player already has a pending invite.");

      return ok({ inviteId: inviteId });
    }

    case "respondInvite": {
      const inviteId = payload.inviteId;
      if (!inviteId) return fail("INVALID_REQUEST", "Missing invite.");

      // Invites live under the *receiver's* private record, so a client can only ever
      // resolve invitations addressed to its own authenticated player id.
      let invite = null;
      await mutate(api, projectId, playerId_(callerId), "invites", (current) => {
        const map = (current && current.map) || {};
        invite = map[inviteId] || null;
        if (invite) delete map[inviteId];
        return { map: map };
      });

      if (!invite) return fail("INVITE_NOT_FOUND", "That invitation no longer exists.");
      if (invite.receiverId !== callerId) return fail("PERMISSION_DENIED", "That invitation is not yours.");
      if (invite.expiresAt < now) return fail("INVITE_EXPIRED", "That invitation expired.");
      if (!payload.accept) return ok({ accepted: false });
      if (social.clanId) return fail("ALREADY_IN_CLAN", "Leave your current clan first.");

      const target = await loadClan(invite.clanId);
      if (!target) return fail("CLAN_NOT_FOUND", "That clan no longer exists.");
      if (target.memberCount >= target.maxMembers) return fail("CLAN_FULL", "That clan is full.");

      const members = await mutate(api, projectId, clanId_(target.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        if (Object.keys(map).length >= target.maxMembers) return { __abort: true };
        map[callerId] = {
          playerId: callerId,
          name: callerName,
          role: ROLE.member,
          joinedAt: now,
          contribution: 0,
          lastActive: now,
        };
        return { map: map };
      });
      if (members && members.__abort) return fail("CLAN_FULL", "That clan is full.");

      social.clanId = target.clanId;
      social.role = ROLE.member;
      social.joinedAt = now;
      await setPlayerSocial(api, projectId, callerId, social);

      const updated = await mutate(api, projectId, clanId_(target.clanId), "profile", (current) => {
        const value = current || target;
        value.memberCount = Object.keys(members.map).length;
        return value;
      });
      await updateIndex(api, projectId, updated.clanId, summarize(updated));
      await appendActivity(api, projectId, updated.clanId, { ts: now, type: "joined", actorName: callerName, text: `${callerName} accepted an invite from ${invite.senderName}.` });
      return ok({ accepted: true, clan: updated });
    }

    case "leave": {
      const gate = await requireMembership(null);
      if (gate.error) return gate.error;
      if (social.role === ROLE.owner && gate.profile.memberCount > 1) {
        return fail("OWNER_MUST_TRANSFER", "Transfer ownership or disband before leaving.");
      }
      if (social.role === ROLE.owner) return await disband(gate.profile);

      const members = await mutate(api, projectId, clanId_(gate.profile.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        delete map[callerId];
        return { map: map };
      });

      const updated = await mutate(api, projectId, clanId_(gate.profile.clanId), "profile", (current) => {
        const value = current || gate.profile;
        value.memberCount = Object.keys(members.map).length;
        value.score = sumContributions(members.map);
        return value;
      });

      social.clanId = null;
      social.role = null;
      social.contribution = 0;
      await setPlayerSocial(api, projectId, callerId, social);
      await updateIndex(api, projectId, updated.clanId, summarize(updated));
      await appendActivity(api, projectId, updated.clanId, { ts: now, type: "left", actorName: callerName, text: `${callerName} left the clan.` });
      await syncClanLeaderboard(context, updated);
      return ok({ left: true });
    }

    case "kick": {
      const gate = await requireMembership(ROLE.officer);
      if (gate.error) return gate.error;
      const targetId = payload.targetPlayerId;
      if (!targetId || targetId === callerId) return fail("INVALID_REQUEST", "Invalid target.");

      const membersItem = await readPrivate(api, projectId, clanId_(gate.profile.clanId), "members");
      const memberMap = (membersItem.value && membersItem.value.map) || {};
      const target = memberMap[targetId];
      if (!target) return fail("NOT_A_MEMBER", "That player is not in your clan.");
      if (ROLE_RANK[target.role] >= ROLE_RANK[social.role]) {
        return fail("PERMISSION_DENIED", "You cannot kick someone of equal or higher rank.");
      }

      const members = await mutate(api, projectId, clanId_(gate.profile.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        delete map[targetId];
        return { map: map };
      });

      const targetSocial = await loadSocial(api, projectId, targetId);
      targetSocial.clanId = null;
      targetSocial.role = null;
      targetSocial.contribution = 0;
      await setPlayerSocial(api, projectId, targetId, targetSocial);

      const updated = await mutate(api, projectId, clanId_(gate.profile.clanId), "profile", (current) => {
        const value = current || gate.profile;
        value.memberCount = Object.keys(members.map).length;
        value.score = sumContributions(members.map);
        return value;
      });
      await updateIndex(api, projectId, updated.clanId, summarize(updated));
      await appendActivity(api, projectId, updated.clanId, { ts: now, type: "kicked", actorName: callerName, text: `${target.name} was removed by ${callerName}.` });
      await syncClanLeaderboard(context, updated);
      return ok({ kicked: true });
    }

    case "setRole": {
      const gate = await requireMembership(ROLE.owner);
      if (gate.error) return gate.error;
      const targetId = payload.targetPlayerId;
      const role = payload.role;
      if (role !== ROLE.officer && role !== ROLE.member) return fail("INVALID_REQUEST", "Role must be Officer or Member.");
      if (!targetId || targetId === callerId) return fail("INVALID_REQUEST", "Invalid target.");

      let found = false;
      await mutate(api, projectId, clanId_(gate.profile.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        if (!map[targetId]) return { __abort: true };
        map[targetId].role = role;
        found = true;
        return { map: map };
      });
      if (!found) return fail("NOT_A_MEMBER", "That player is not in your clan.");

      const targetSocial = await loadSocial(api, projectId, targetId);
      targetSocial.role = role;
      await setPlayerSocial(api, projectId, targetId, targetSocial);
      await appendActivity(api, projectId, gate.profile.clanId, { ts: now, type: "role", actorName: callerName, text: `${targetSocial.name || "A member"} is now ${role}.` });
      return ok({ role: role });
    }

    case "transferOwnership": {
      const gate = await requireMembership(ROLE.owner);
      if (gate.error) return gate.error;
      const targetId = payload.targetPlayerId;
      if (!targetId || targetId === callerId) return fail("INVALID_REQUEST", "Invalid target.");

      let targetName = null;
      const members = await mutate(api, projectId, clanId_(gate.profile.clanId), "members", (current) => {
        const map = (current && current.map) || {};
        if (!map[targetId]) return { __abort: true };
        map[targetId].role = ROLE.owner;
        map[callerId].role = ROLE.officer;
        targetName = map[targetId].name;
        return { map: map };
      });
      if (members && members.__abort) return fail("NOT_A_MEMBER", "That player is not in your clan.");

      const targetSocial = await loadSocial(api, projectId, targetId);
      targetSocial.role = ROLE.owner;
      await setPlayerSocial(api, projectId, targetId, targetSocial);
      social.role = ROLE.officer;
      await setPlayerSocial(api, projectId, callerId, social);

      const updated = await mutate(api, projectId, clanId_(gate.profile.clanId), "profile", (current) => {
        const value = current || gate.profile;
        value.ownerId = targetId;
        value.ownerName = targetName;
        return value;
      });
      await appendActivity(api, projectId, updated.clanId, { ts: now, type: "owner", actorName: callerName, text: `${targetName} is now the clan leader.` });
      return ok({ ownerId: targetId });
    }

    case "updateSettings": {
      const gate = await requireMembership(ROLE.officer);
      if (gate.error) return gate.error;

      const updated = await mutate(api, projectId, clanId_(gate.profile.clanId), "profile", (current) => {
        const value = current || gate.profile;
        if (typeof payload.description === "string") value.description = sanitize(payload.description, CONFIG.descriptionMax);
        if (typeof payload.motd === "string") value.motd = sanitize(payload.motd, CONFIG.motdMax);
        if (typeof payload.isPublic === "boolean" && social.role === ROLE.owner) value.isPublic = payload.isPublic;
        if (payload.emblemId !== undefined) value.emblemId = Math.max(0, Math.min(11, payload.emblemId | 0));
        return value;
      });
      await updateIndex(api, projectId, updated.clanId, summarize(updated));
      return ok({ clan: updated });
    }

    case "disband": {
      const gate = await requireMembership(ROLE.owner);
      if (gate.error) return gate.error;
      return await disband(gate.profile);
    }

    default:
      return fail("UNKNOWN_ACTION", `Unsupported action '${params.action}'.`);
  }

  async function disband(profile) {
    const membersItem = await readPrivate(api, projectId, clanId_(profile.clanId), "members");
    const memberMap = (membersItem.value && membersItem.value.map) || {};

    for (const memberId of Object.keys(memberMap)) {
      const memberSocial = await loadSocial(api, projectId, memberId);
      memberSocial.clanId = null;
      memberSocial.role = null;
      memberSocial.contribution = 0;
      await setPlayerSocial(api, projectId, memberId, memberSocial);
    }

    await mutate(api, projectId, INDEX_ID, "tags", (current) => {
      const map = (current && current.map) || {};
      delete map[profile.tag];
      return { map: map };
    });
    await updateIndex(api, projectId, profile.clanId, null);

    for (const key of ["profile", "members", "chat", "activity", "requests"]) {
      try {
        await api.deletePrivateCustomItem(key, projectId, clanId_(profile.clanId));
      } catch (err) {
        // Already absent - deletion is idempotent for our purposes.
      }
    }

    try {
      const leaderboards = new LeaderboardsApi(context);
      await leaderboards.addLeaderboardPlayerScore(context.projectId, CONFIG.clanLeaderboardId, profile.clanId, {
        score: 0,
        metadata: { name: profile.name, tag: profile.tag, memberCount: 0, level: profile.level, disbanded: true },
      });
    } catch (err) {
      // Leaderboard cleanup is best-effort.
    }

    return ok({ disbanded: true });
  }
};

module.exports.params = { action: "String", payload: "JSON" };

// schema-rev 2
