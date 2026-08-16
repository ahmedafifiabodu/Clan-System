using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using Unity.Services.CloudCode;
using Unity.Services.Vivox;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Routes every Vivox token request through Cloud Code.
    ///
    /// This is what makes clan channels private. The Vivox service refuses any join without a signed
    /// token, and the signing key lives only in Secret Manager, reachable only by
    /// <c>SOCIAL_VivoxToken.js</c>. That script rebuilds the allowed channel name from the caller's
    /// real clan membership, so a client that edits a clan id locally is simply never issued a token.
    /// Registering this provider also disables the SDK's default token path.
    /// </summary>
    public class CloudCodeVivoxTokenProvider : IVivoxTokenProvider
    {
        private readonly ClanSystemConfig _config;

        public CloudCodeVivoxTokenProvider(ClanSystemConfig config)
        {
            _config = config != null ? config : throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Last failure reported by the server, so the UI can explain why voice is unavailable.
        /// </summary>
        public string LastError { get; private set; }

        public SocialErrorCode LastErrorCode { get; private set; }

        public async Task<string> GetTokenAsync(
            string issuer = null,
            TimeSpan? expiration = null,
            string targetUserUri = null,
            string action = null,
            string channelUri = null,
            string fromUserUri = null,
            string realm = null)
        {
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { "action", action ?? string.Empty },
                { "channelUri", channelUri ?? string.Empty },
                { "fromUserUri", fromUserUri ?? string.Empty },
                { "targetUserUri", targetUserUri ?? string.Empty },
                { "expirationSeconds", expiration.HasValue ? (int)expiration.Value.TotalSeconds : 90 },
            };

            try
            {
                SocialEnvelope<VivoxTokenResponse> envelope =
                    await CloudCodeService.Instance.CallEndpointAsync<SocialEnvelope<VivoxTokenResponse>>(
                        _config.VivoxTokenScript, args);

                if (envelope == null || !envelope.IsOk)
                {
                    LastErrorCode = SocialErrorMapper.FromServerCode(envelope != null ? envelope.Code : null);
                    LastError = envelope != null && !string.IsNullOrEmpty(envelope.Message)
                        ? envelope.Message
                        : SocialErrorMapper.Describe(LastErrorCode);

                    Debug.LogWarning($"[ClanSystem] Vivox token refused for '{action}' on '{channelUri}': {LastError}");

                    // Returning null makes the Vivox SDK fail the operation, which is the correct
                    // outcome: no token means no channel access.
                    return null;
                }

                LastError = null;
                LastErrorCode = SocialErrorCode.None;
                return envelope.Data != null ? envelope.Data.Token : null;
            }
            catch (Exception exception)
            {
                LastErrorCode = SocialErrorMapper.FromException(exception);
                LastError = SocialErrorMapper.Describe(LastErrorCode);
                Debug.LogWarning($"[ClanSystem] Vivox token request failed: {exception.Message}");
                return null;
            }
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class VivoxTokenResponse
        {
            [Newtonsoft.Json.JsonProperty("token")] public string Token { get; set; }
            [Newtonsoft.Json.JsonProperty("channelName")] public string ChannelName { get; set; }
        }
    }
}
