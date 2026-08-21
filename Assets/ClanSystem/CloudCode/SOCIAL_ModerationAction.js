const { DataApi } = require("@unity-services/cloud-save-1.4");

// Target of the "Moderation actions" hook in the dashboard
// (Vivox Voice and Text Chat -> Safe Text -> Moderation actions -> Cloud Code Script).
//
// Unity invokes this whenever a moderation action is applied to or lifted from a player. The action
// itself is recorded by Unity; this script's job is to make the action mean something inside the
// game: it writes a moderation record the rest of the backend honours.
//
// The teeth are in SOCIAL_VivoxToken.js, which refuses to mint a Vivox token for a player whose
// record blocks chat or voice. Because Vivox will not admit a client without a token, a muted or
// banned player cannot rejoin a channel no matter what their client does.
const CONFIG = {
  // Dashboard action names -> what they deny. Matches the default action set.
  actionEffects: {
    "ban from game": { text: true, voice: true, game: true },
    "block from voice & chat": { text: true, voice: true, game: false },
    "mute all": { text: true, voice: true, game: false },
    "text mute": { text: true, voice: false, game: false },
    "voice mute": { text: false, voice: true, game: false },
  },
};

const ok = (data) => ({ ok: true, code: "OK", message: "", data: data || {} });
const fail = (code, message) => ({ ok: false, code: code, message: message, data: {} });

// The moderation record is protected player data: the server writes it, the player may read it.
// Reading it is harmless - it is the player's own restriction and they are already living under it -
// and being unable to write it is the whole point. Protected data is also scoped to the player, so
// the record goes away when the player is deleted rather than outliving them as an orphan item.
async function readPlayer(api, projectId, playerId, key) {
  const res = await api.getProtectedItems(projectId, playerId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return { value: null, writeLock: null };
  return { value: results[0].value, writeLock: results[0].writeLock };
}

async function writePlayer(api, projectId, playerId, key, value, writeLock) {
  const body = { key: key, value: value };
  if (writeLock) body.writeLock = writeLock;
  await api.setProtectedItem(projectId, playerId, body);
}

// The event shape is owned by the Moderation service and can carry different field names across
// versions, so every lookup is tolerant rather than assuming one spelling.
function firstDefined(source, names) {
  if (!source) return null;
  for (const name of names) {
    if (source[name] !== undefined && source[name] !== null && source[name] !== "") {
      return source[name];
    }
  }
  return null;
}

function resolveEffects(actionName) {
  if (!actionName) return null;
  const key = String(actionName).trim().toLowerCase();
  if (CONFIG.actionEffects[key]) return CONFIG.actionEffects[key];

  // Unknown or custom action: fall back to what the name implies rather than ignoring it.
  const denyText = key.indexOf("text") >= 0 || key.indexOf("chat") >= 0 || key.indexOf("ban") >= 0 || key.indexOf("mute all") >= 0;
  const denyVoice = key.indexOf("voice") >= 0 || key.indexOf("ban") >= 0 || key.indexOf("mute all") >= 0;
  if (!denyText && !denyVoice) return null;
  return { text: denyText, voice: denyVoice, game: key.indexOf("ban") >= 0 };
}

function resolveExpiry(event, now) {
  const explicit = firstDefined(event, ["expiresAt", "expiryDate", "expiresOn", "endsAt"]);
  if (explicit) {
    const parsed = typeof explicit === "number" ? explicit : Date.parse(explicit);
    if (!isNaN(parsed)) return parsed;
  }

  const durationSeconds = Number(firstDefined(event, ["durationSeconds", "duration"]));
  if (isFinite(durationSeconds) && durationSeconds > 0) {
    return now + durationSeconds * 1000;
  }

  // Convention shared with SOCIAL_VivoxToken.js:
  //   0 -> no restriction,  -1 -> permanent,  > 0 -> expiry timestamp.
  // No expiry supplied means permanent.
  return -1;
}

module.exports = async ({ params, context, logger }) => {
  const event = params.event || {};
  const now = Date.now();

  // Log the raw event once: the exact field names differ between moderation versions and this is
  // the fastest way to confirm the contract against a real incident.
  logger.info("Moderation action received", { event: JSON.stringify(event).slice(0, 1500) });

  const playerId = firstDefined(event, ["playerId", "userId", "targetPlayerId", "subjectId", "reportedPlayerId"]);
  if (!playerId) {
    return fail("INVALID_REQUEST", "Moderation event carried no player id.");
  }

  const actionName = firstDefined(event, ["actionName", "action", "moderationAction", "name", "actionType"]);
  const isLifted = String(firstDefined(event, ["state", "status", "eventType", "type"]) || "")
    .toLowerCase()
    .match(/lift|revok|remov|expir|delete|end/) !== null;

  const api = new DataApi(context);
  const projectId = context.projectId;

  if (isLifted) {
    const current = await readPlayer(api, projectId, playerId, "moderation");
    await writePlayer(api, projectId, playerId, "moderation", {
      textBlockedUntil: 0,
      voiceBlockedUntil: 0,
      gameBannedUntil: 0,
      lastAction: actionName || "lifted",
      updatedAt: now,
    }, current.writeLock);

    logger.info("Moderation restrictions cleared", { playerId: playerId, action: actionName });
    return ok({ playerId: playerId, cleared: true });
  }

  const effects = resolveEffects(actionName);
  if (!effects) {
    // A positive or informational action - nothing to enforce.
    return ok({ playerId: playerId, applied: false, reason: "No restriction implied by '" + actionName + "'." });
  }

  const expiresAt = resolveExpiry(event, now);
  const current = await readPlayer(api, projectId, playerId, "moderation");
  const record = current.value || {};

  if (effects.text) record.textBlockedUntil = expiresAt;
  if (effects.voice) record.voiceBlockedUntil = expiresAt;
  if (effects.game) record.gameBannedUntil = expiresAt;
  record.lastAction = actionName;
  record.updatedAt = now;

  await writePlayer(api, projectId, playerId, "moderation", record, current.writeLock);

  logger.info("Moderation restrictions applied", {
    playerId: playerId,
    action: actionName,
    expiresAt: expiresAt,
  });

  return ok({
    playerId: playerId,
    applied: true,
    action: actionName,
    textBlocked: !!effects.text,
    voiceBlocked: !!effects.voice,
    gameBanned: !!effects.game,
    expiresAt: expiresAt,
  });
};

module.exports.params = { event: "JSON" };
