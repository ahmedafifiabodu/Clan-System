using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using Unity.Services.CloudCode;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Talks to the four Cloud Code scripts that own all clan, chat and score state.
    /// Adds the timeout, cancellation and retry behaviour the Cloud Code SDK does not provide,
    /// and normalises every outcome into a <see cref="SocialResult"/>.
    /// </summary>
    public class CloudCodeSocialBackend : ISocialBackend
    {
        private const string _actionKey = "action";
        private const string _payloadKey = "payload";

        private readonly ClanSystemConfig _config;

        public CloudCodeSocialBackend(ClanSystemConfig config)
        {
            _config = config != null ? config : throw new ArgumentNullException(nameof(config));
        }

        public Task<SocialResult<SocialSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            return CallAsync<SocialSnapshot>(_config.ClanQueryScript, "me", null, cancellationToken);
        }

        public Task<SocialResult<ClanDetail>> GetClanAsync(string clanId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "clanId", clanId } };
            return CallAsync<ClanDetail>(_config.ClanQueryScript, "getClan", payload, cancellationToken);
        }

        public Task<SocialResult<ClanSearchPage>> SearchClansAsync(string query, bool onlyPublic, int offset, int limit, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "query", query ?? string.Empty },
                { "onlyPublic", onlyPublic },
                { "offset", offset },
                { "limit", limit },
            };
            return CallAsync<ClanSearchPage>(_config.ClanQueryScript, "search", payload, cancellationToken);
        }

        public async Task<SocialResult<List<ClanMember>>> GetMembersAsync(CancellationToken cancellationToken)
        {
            SocialResult<MembersResponse> result = await CallAsync<MembersResponse>(_config.ClanQueryScript, "members", null, cancellationToken);
            if (!result.IsSuccess)
            {
                return SocialResult<List<ClanMember>>.Failure(result.Error, result.Message);
            }

            return SocialResult<List<ClanMember>>.Success(result.Value?.Members ?? new List<ClanMember>());
        }

        public async Task<SocialResult<List<ClanActivityEntry>>> GetActivityAsync(int limit, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "limit", limit } };
            SocialResult<ActivityResponse> result = await CallAsync<ActivityResponse>(_config.ClanQueryScript, "activity", payload, cancellationToken);
            if (!result.IsSuccess)
            {
                return SocialResult<List<ClanActivityEntry>>.Failure(result.Error, result.Message);
            }

            return SocialResult<List<ClanActivityEntry>>.Success(result.Value?.Entries ?? new List<ClanActivityEntry>());
        }

        public async Task<SocialResult<List<ClanJoinRequest>>> GetJoinRequestsAsync(CancellationToken cancellationToken)
        {
            SocialResult<JoinRequestsResponse> result = await CallAsync<JoinRequestsResponse>(_config.ClanQueryScript, "joinRequests", null, cancellationToken);
            if (!result.IsSuccess)
            {
                return SocialResult<List<ClanJoinRequest>>.Failure(result.Error, result.Message);
            }

            return SocialResult<List<ClanJoinRequest>>.Success(result.Value?.Requests ?? new List<ClanJoinRequest>());
        }

        public async Task<SocialResult<List<PlayerSocialInfo>>> GetPlayersInfoAsync(IReadOnlyList<string> playerIds, CancellationToken cancellationToken)
        {
            if (playerIds == null || playerIds.Count == 0)
            {
                return SocialResult<List<PlayerSocialInfo>>.Success(new List<PlayerSocialInfo>());
            }

            List<string> ids = new List<string>(playerIds.Count);
            for (int i = 0; i < playerIds.Count; i++)
            {
                ids.Add(playerIds[i]);
            }

            Dictionary<string, object> payload = new Dictionary<string, object> { { "playerIds", ids } };
            SocialResult<PlayersInfoResponse> result = await CallAsync<PlayersInfoResponse>(_config.ClanQueryScript, "playersInfo", payload, cancellationToken);
            if (!result.IsSuccess)
            {
                return SocialResult<List<PlayerSocialInfo>>.Failure(result.Error, result.Message);
            }

            return SocialResult<List<PlayerSocialInfo>>.Success(result.Value?.Players ?? new List<PlayerSocialInfo>());
        }

        public async Task<SocialResult<ClanProfile>> CreateClanAsync(string name, string tag, string description, bool isPublic, int emblemId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "name", name },
                { "tag", tag },
                { "description", description ?? string.Empty },
                { "isPublic", isPublic },
                { "emblemId", emblemId },
            };
            SocialResult<ClanMutationResponse> result = await CallAsync<ClanMutationResponse>(_config.ClanCommandScript, "create", payload, cancellationToken);
            return ToClanResult(result);
        }

        public async Task<SocialResult<ClanProfile>> JoinClanAsync(string clanId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "clanId", clanId } };
            SocialResult<ClanMutationResponse> result = await CallAsync<ClanMutationResponse>(_config.ClanCommandScript, "join", payload, cancellationToken);
            return ToClanResult(result);
        }

        public Task<SocialResult> RequestJoinAsync(string clanId, string message, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "clanId", clanId },
                { "message", message ?? string.Empty },
            };
            return CallVoidAsync(_config.ClanCommandScript, "requestJoin", payload, cancellationToken);
        }

        public Task<SocialResult> HandleJoinRequestAsync(string playerId, bool accept, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "playerId", playerId },
                { "accept", accept },
            };
            return CallVoidAsync(_config.ClanCommandScript, "handleJoinRequest", payload, cancellationToken);
        }

        public Task<SocialResult> InvitePlayerAsync(string targetPlayerId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "targetPlayerId", targetPlayerId } };
            return CallVoidAsync(_config.ClanCommandScript, "invite", payload, cancellationToken);
        }

        public Task<SocialResult> RespondToInviteAsync(string inviteId, bool accept, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "inviteId", inviteId },
                { "accept", accept },
            };
            return CallVoidAsync(_config.ClanCommandScript, "respondInvite", payload, cancellationToken);
        }

        public Task<SocialResult> LeaveClanAsync(CancellationToken cancellationToken)
        {
            return CallVoidAsync(_config.ClanCommandScript, "leave", null, cancellationToken);
        }

        public Task<SocialResult> KickMemberAsync(string targetPlayerId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "targetPlayerId", targetPlayerId } };
            return CallVoidAsync(_config.ClanCommandScript, "kick", payload, cancellationToken);
        }

        public Task<SocialResult> SetMemberRoleAsync(string targetPlayerId, ClanRole role, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "targetPlayerId", targetPlayerId },
                { "role", SocialEnum.ToWire(role) },
            };
            return CallVoidAsync(_config.ClanCommandScript, "setRole", payload, cancellationToken);
        }

        public Task<SocialResult> TransferOwnershipAsync(string targetPlayerId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "targetPlayerId", targetPlayerId } };
            return CallVoidAsync(_config.ClanCommandScript, "transferOwnership", payload, cancellationToken);
        }

        public async Task<SocialResult<ClanProfile>> UpdateClanSettingsAsync(string description, string motd, bool? isPublic, int? emblemId, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>();
            if (description != null)
            {
                payload.Add("description", description);
            }

            if (motd != null)
            {
                payload.Add("motd", motd);
            }

            if (isPublic.HasValue)
            {
                payload.Add("isPublic", isPublic.Value);
            }

            if (emblemId.HasValue)
            {
                payload.Add("emblemId", emblemId.Value);
            }

            SocialResult<ClanMutationResponse> result = await CallAsync<ClanMutationResponse>(_config.ClanCommandScript, "updateSettings", payload, cancellationToken);
            return ToClanResult(result);
        }

        public Task<SocialResult> DisbandClanAsync(CancellationToken cancellationToken)
        {
            return CallVoidAsync(_config.ClanCommandScript, "disband", null, cancellationToken);
        }

        public Task<SocialResult<LeaderboardPage>> GetPlayerLeaderboardAsync(int offset, int limit, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "offset", offset },
                { "limit", limit },
            };
            return CallAsync<LeaderboardPage>(_config.LeaderboardScript, "players", payload, cancellationToken);
        }

        public Task<SocialResult<LeaderboardPage>> GetClanLeaderboardAsync(int offset, int limit, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>
            {
                { "offset", offset },
                { "limit", limit },
            };
            return CallAsync<LeaderboardPage>(_config.LeaderboardScript, "clans", payload, cancellationToken);
        }

        public Task<SocialResult<ScoreSubmitResponse>> SubmitScoreAsync(long delta, CancellationToken cancellationToken)
        {
            Dictionary<string, object> payload = new Dictionary<string, object> { { "delta", delta } };
            return CallAsync<ScoreSubmitResponse>(_config.LeaderboardScript, "submitScore", payload, cancellationToken);
        }

        private static SocialResult<ClanProfile> ToClanResult(SocialResult<ClanMutationResponse> result)
        {
            if (!result.IsSuccess)
            {
                return SocialResult<ClanProfile>.Failure(result.Error, result.Message);
            }

            return SocialResult<ClanProfile>.Success(result.Value?.Clan);
        }

        private async Task<SocialResult> CallVoidAsync(string script, string action, Dictionary<string, object> payload, CancellationToken cancellationToken)
        {
            SocialResult<EmptyResponse> result = await CallAsync<EmptyResponse>(script, action, payload, cancellationToken);
            if (!result.IsSuccess)
            {
                return SocialResult.Failure(result.Error, result.Message);
            }

            return SocialResult.Success();
        }

        private async Task<SocialResult<T>> CallAsync<T>(string script, string action, Dictionary<string, object> payload, CancellationToken cancellationToken)
        {
            Dictionary<string, object> args = new Dictionary<string, object>
            {
                { _actionKey, action },
                { _payloadKey, payload ?? new Dictionary<string, object>() },
            };

            int attempts = Mathf.Max(1, _config.RequestRetryCount + 1);
            SocialErrorCode lastError = SocialErrorCode.Unknown;
            string lastMessage = string.Empty;

            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return SocialResult<T>.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
                }

                try
                {
                    SocialEnvelope<T> envelope = await RunWithTimeoutAsync<T>(script, args, cancellationToken);
                    if (envelope == null)
                    {
                        lastError = SocialErrorCode.Unknown;
                        lastMessage = SocialErrorMapper.Describe(lastError);
                        continue;
                    }

                    if (envelope.IsOk)
                    {
                        return SocialResult<T>.Success(envelope.Data);
                    }

                    SocialErrorCode mapped = SocialErrorMapper.FromServerCode(envelope.Code);
                    string message = string.IsNullOrEmpty(envelope.Message) ? SocialErrorMapper.Describe(mapped) : envelope.Message;

                    // A rejection is a decision, not a glitch: never retry it.
                    return SocialResult<T>.Failure(mapped, message);
                }
                catch (OperationCanceledException)
                {
                    return SocialResult<T>.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
                }
                catch (Exception exception)
                {
                    lastError = SocialErrorMapper.FromException(exception);
                    lastMessage = SocialErrorMapper.Describe(lastError);

                    bool isRetryable = lastError == SocialErrorCode.NetworkUnavailable
                        || lastError == SocialErrorCode.Timeout
                        || lastError == SocialErrorCode.ServiceUnavailable;

                    if (!isRetryable || attempt == attempts - 1)
                    {
                        Debug.LogWarning($"[ClanSystem] {script}/{action} failed: {exception.GetType().Name} - {exception.Message}");
                        return SocialResult<T>.Failure(lastError, lastMessage);
                    }

                    await Task.Delay(250 * (attempt + 1), cancellationToken);
                }
            }

            return SocialResult<T>.Failure(lastError, lastMessage);
        }

        private async Task<SocialEnvelope<T>> RunWithTimeoutAsync<T>(string script, Dictionary<string, object> args, CancellationToken cancellationToken)
        {
            // The Cloud Code SDK takes no cancellation token, so the call is raced against a timeout
            // and the caller's token; an abandoned call is left to complete in the background.
            Task<SocialEnvelope<T>> call = CloudCodeService.Instance.CallEndpointAsync<SocialEnvelope<T>>(script, args);
            Task timeout = Task.Delay(TimeSpan.FromSeconds(_config.RequestTimeoutSeconds), cancellationToken);
            Task finished = await Task.WhenAny(call, timeout);

            if (finished != call)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"Cloud Code script '{script}' timed out.");
            }

            return await call;
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class EmptyResponse
        {
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class ClanMutationResponse
        {
            [Newtonsoft.Json.JsonProperty("clan")] public ClanProfile Clan { get; set; }
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class MembersResponse
        {
            [Newtonsoft.Json.JsonProperty("members")] public List<ClanMember> Members { get; set; }
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class ActivityResponse
        {
            [Newtonsoft.Json.JsonProperty("entries")] public List<ClanActivityEntry> Entries { get; set; }
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class JoinRequestsResponse
        {
            [Newtonsoft.Json.JsonProperty("requests")] public List<ClanJoinRequest> Requests { get; set; }
        }

        [Newtonsoft.Json.JsonObject(MissingMemberHandling = Newtonsoft.Json.MissingMemberHandling.Ignore)]
        private class PlayersInfoResponse
        {
            [Newtonsoft.Json.JsonProperty("players")] public List<PlayerSocialInfo> Players { get; set; }
        }
    }
}
