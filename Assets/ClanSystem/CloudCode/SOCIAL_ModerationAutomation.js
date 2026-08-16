const { DataApi } = require("@unity-services/cloud-save-1.4");

// Target of the "Automation" hook in the dashboard
// (Vivox Voice and Text Chat -> Safe Text -> Automation -> Cloud Code Script).
//
// Unity invokes this for each moderation incident raised by the profanity/Safe Text filter. The
// script decides what the game does about it, without waiting for a human moderator.
//
// Policy here is deliberately conservative: low-severity incidents are recorded only, and
// restrictions escalate with severity and with how often the player has offended before. Nothing is
// permanent from automation alone - a permanent ban stays a human decision.
const CONFIG = {
  // severity -> restriction in minutes. 0 means record only.
  severityMinutes: {
    low: 0,
    medium: 60,
    high: 24 * 60,
    critical: 7 * 24 * 60,
  },
  // Each prior incident multiplies the restriction.
  repeatMultiplier: 2,
  maxMinutes: 30 * 24 * 60,
  incidentHistoryMax: 50,
};

const ok = (data) => ({ ok: true, code: "OK", message: "", data: data || {} });
const fail = (code, message) => ({ ok: false, code: code, message: message, data: {} });

const playerKey = (id) => `player-${id}`;

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

// Field names vary between moderation versions, so every lookup tolerates several spellings.
function firstDefined(source, names) {
  if (!source) return null;
  for (const name of names) {
    if (source[name] !== undefined && source[name] !== null && source[name] !== "") {
      return source[name];
    }
  }
  return null;
}

function normaliseSeverity(value) {
  const text = String(value || "").trim().toLowerCase();
  if (CONFIG.severityMinutes[text] !== undefined) return text;

  // Numeric severities: map onto the same buckets.
  const numeric = Number(value);
  if (isFinite(numeric)) {
    if (numeric >= 4) return "critical";
    if (numeric >= 3) return "high";
    if (numeric >= 2) return "medium";
    return "low";
  }

  if (text.indexOf("sever") >= 0 || text.indexOf("critical") >= 0) return "critical";
  if (text.indexOf("high") >= 0) return "high";
  if (text.indexOf("med") >= 0 || text.indexOf("moderate") >= 0) return "medium";
  return "low";
}

module.exports = async ({ params, context, logger }) => {
  const event = params.event || {};
  const now = Date.now();

  // Logged in full once so the real incident shape can be confirmed against live data.
  logger.info("Moderation incident received", { event: JSON.stringify(event).slice(0, 1500) });

  const playerId = firstDefined(event, ["playerId", "userId", "reportedPlayerId", "subjectId", "offenderId"]);
  if (!playerId) {
    return fail("INVALID_REQUEST", "Moderation incident carried no player id.");
  }

  const incidentId = firstDefined(event, ["incidentId", "id", "caseId"]);
  const severityRaw = firstDefined(event, ["severity", "severityLevel", "riskLevel", "score"]);
  const severity = normaliseSeverity(severityRaw);
  const offense = firstDefined(event, ["offenseType", "offence", "category", "reason", "violationType"]);

  const api = new DataApi(context);
  const projectId = context.projectId;

  // Incident history drives escalation, and doubles as an audit trail per player.
  const historyItem = await readPrivate(api, projectId, playerKey(playerId), "incidents");
  const history = (historyItem.value && historyItem.value.entries) || [];

  history.unshift({
    incidentId: incidentId,
    severity: severity,
    offense: offense,
    ts: now,
  });

  const trimmed = history.slice(0, CONFIG.incidentHistoryMax);
  await writePrivate(api, projectId, playerKey(playerId), "incidents", { entries: trimmed }, historyItem.writeLock);

  const baseMinutes = CONFIG.severityMinutes[severity] || 0;
  if (baseMinutes === 0) {
    logger.info("Incident recorded without restriction", { playerId: playerId, severity: severity });
    return ok({ playerId: playerId, severity: severity, restricted: false, priorIncidents: trimmed.length - 1 });
  }

  // Escalate on repeat offences, but never past the ceiling.
  const priorCount = Math.max(0, trimmed.length - 1);
  const multiplier = Math.pow(CONFIG.repeatMultiplier, Math.min(priorCount, 5));
  const minutes = Math.min(CONFIG.maxMinutes, Math.round(baseMinutes * multiplier));
  const expiresAt = now + minutes * 60 * 1000;

  const current = await readPrivate(api, projectId, playerKey(playerId), "moderation");
  const record = current.value || {};

  // Text offences mute text; voice offences mute voice. Anything unclear restricts both, because
  // Safe Text and Safe Voice both feed this hook.
  const offenseText = String(offense || "").toLowerCase();
  const isVoiceOnly = offenseText.indexOf("voice") >= 0 || offenseText.indexOf("audio") >= 0;
  const isTextOnly = offenseText.indexOf("text") >= 0 || offenseText.indexOf("chat") >= 0 || offenseText.indexOf("profan") >= 0;

  if (!isVoiceOnly) record.textBlockedUntil = Math.max(record.textBlockedUntil || 0, expiresAt);
  if (!isTextOnly) record.voiceBlockedUntil = Math.max(record.voiceBlockedUntil || 0, expiresAt);

  record.lastAction = "automation:" + severity;
  record.lastIncidentId = incidentId;
  record.updatedAt = now;

  await writePrivate(api, projectId, playerKey(playerId), "moderation", record, current.writeLock);

  logger.info("Automated restriction applied", {
    playerId: playerId,
    severity: severity,
    minutes: minutes,
    priorIncidents: priorCount,
  });

  return ok({
    playerId: playerId,
    incidentId: incidentId,
    severity: severity,
    restricted: true,
    minutes: minutes,
    expiresAt: expiresAt,
    priorIncidents: priorCount,
  });
};

module.exports.params = { event: "JSON" };
