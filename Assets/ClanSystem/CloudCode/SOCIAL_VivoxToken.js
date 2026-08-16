const { DataApi } = require("@unity-services/cloud-save-1.4");
const vivox = require("@unity-services/vivox-0.1");

// The single authority for Vivox channel access.
//
// The Vivox client cannot join any channel without a signed token, and only this script holds the
// signing key. Every request is checked against the caller's *real* clan membership, read from
// Cloud Save private custom data - never from the request. A player who edits a clan id client-side
// simply fails to get a token, so the join is refused by the Vivox service itself.
const CONFIG = {
  globalChannelName: "global",
  clanChannelPrefix: "clan_",
  tokenLifetimeSeconds: 90,
  allowedActions: ["login", "join", "join_muted"],
};

// Vivox credentials come from Secret Manager only (VIVOX_ISSUER / VIVOX_KEY).
//
// The signing key must never live in this repository or in a Unity asset: the repo is shared and a
// Unity asset ships inside the game binary, where anyone could extract the key and mint a token for
// any clan channel - defeating the entire authorisation model.

const ok = (data) => ({ ok: true, code: "OK", message: "", data: data || {} });
const fail = (code, message) => ({ ok: false, code: code, message: message, data: {} });

async function readPrivate(api, projectId, customId, key) {
  const res = await api.getPrivateCustomItems(projectId, customId, [key]);
  const results = (res && res.data && res.data.results) || [];
  if (results.length === 0) return null;
  return results[0].value;
}

// Vivox URIs look like sip:confctl-g-{issuer}.{channelName}.{environmentId}@{domain}
//
// UGS appends the environment id to every channel name so environments stay isolated. The logical
// channel is therefore the segment between the issuer and that suffix - comparing the whole
// remainder against "global" would reject every legitimate join.
function channelNameFromUri(uri) {
  if (typeof uri !== "string" || uri.length === 0) return null;
  const at = uri.indexOf("@");
  if (at < 0) return null;

  const body = uri.substring(0, at);
  const dot = body.indexOf(".");
  if (dot < 0) return null;

  const remainder = body.substring(dot + 1);
  if (remainder.length === 0) return null;

  const nextDot = remainder.indexOf(".");
  const name = nextDot < 0 ? remainder : remainder.substring(0, nextDot);
  return name.length > 0 ? name : null;
}

module.exports = async ({ params, context, logger, secretManager }) => {
  const callerId = context.playerId;
  if (!callerId) return fail("UNAUTHENTICATED", "No player context.");

  const action = typeof params.action === "string" ? params.action.toLowerCase() : "";
  if (CONFIG.allowedActions.indexOf(action) < 0) {
    // kick and mute are moderation actions; a client may never mint them.
    return fail("PERMISSION_DENIED", `Action '${params.action}' is not allowed from a client.`);
  }

  const fromUserUri = params.fromUserUri || "";
  const channelUri = params.channelUri || "";

  // The token must act as the caller, never as another player.
  if (fromUserUri.length > 0 && fromUserUri.indexOf(callerId) < 0) {
    return fail("PERMISSION_DENIED", "Token requested for a different player.");
  }

  let channelName = null;
  if (action !== "login") {
    channelName = channelNameFromUri(channelUri);
    if (!channelName) return fail("INVALID_REQUEST", "Unrecognised channel URI.");

    if (channelName.indexOf(CONFIG.clanChannelPrefix) === 0) {
      const api = new DataApi(context);
      const social = await readPrivate(api, context.projectId, `player-${callerId}`, "social");
      const realClanId = (social && social.clanId) || null;

      if (!realClanId) {
        return fail("NOT_IN_CLAN", "You are not in a clan.");
      }

      // Authorisation happens here: the only clan channel this player can ever be granted is the
      // one matching the clan the server says they belong to right now.
      const allowedChannel = CONFIG.clanChannelPrefix + realClanId;
      if (channelName !== allowedChannel) {
        logger.warning("Rejected clan channel token request", {
          playerId: callerId,
          requested: channelName,
        });
        return fail("PERMISSION_DENIED", "You are not a member of that clan.");
      }
    } else if (channelName !== CONFIG.globalChannelName) {
      return fail("PERMISSION_DENIED", "Unknown channel.");
    }
  }

  let issuer = null;
  let key = null;
  try {
    issuer = (await secretManager.getSecret("VIVOX_ISSUER")).value;
    key = (await secretManager.getSecret("VIVOX_KEY")).value;
  } catch (err) {
    logger.error("Vivox credentials missing from Secret Manager", { "error.message": err.message });
  }

  if (!issuer || !key) {
    return fail("VOICE_NOT_CONFIGURED", "Voice service is not configured. Add VIVOX_ISSUER and VIVOX_KEY in Secret Manager.");
  }

  const requested = Number(params.expirationSeconds);
  const lifetime = isFinite(requested) && requested > 0
    ? Math.min(CONFIG.tokenLifetimeSeconds, Math.floor(requested))
    : CONFIG.tokenLifetimeSeconds;

  const payload = {
    iss: issuer,
    exp: Math.floor(Date.now() / 1000) + lifetime,
    vxa: action,
    vxi: Math.floor(Math.random() * 1e9),
    f: fromUserUri,
  };

  if (channelUri) payload.t = channelUri;

  // The SDK surface has shifted between versions, so try the documented shapes in order rather
  // than guessing one and failing opaquely.
  let token = null;
  const attempts = [];

  const strategies = [
    ["new TokenApi(context)", () => new vivox.TokenApi(context).generateVivoxToken(key, payload)],
    ["new TokenApi()", () => new vivox.TokenApi().generateVivoxToken(key, payload)],
    ["TokenApi static", () => vivox.TokenApi.generateVivoxToken(key, payload)],
    ["module function", () => vivox.generateVivoxToken(key, payload)],
  ];

  for (const [label, run] of strategies) {
    try {
      const candidate = run();
      if (candidate) {
        token = candidate;
        break;
      }
      attempts.push(label + ": empty");
    } catch (err) {
      attempts.push(label + ": " + (err && err.message ? err.message : "threw"));
    }
  }

  if (!token) {
    logger.error("Vivox token generation failed", { attempts: attempts.join(" | ") });
    return fail("VOICE_UNAVAILABLE", "Could not issue a voice token. " + attempts.join(" | "));
  }

  return ok({ token: token, channelName: channelName, action: action });
};

module.exports.params = {
  action: "String",
  channelUri: "String",
  fromUserUri: "String",
  targetUserUri: "String",
  expirationSeconds: "Numeric",
};
