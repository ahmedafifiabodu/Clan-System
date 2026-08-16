using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Orchestrates the social stack: sign-in, snapshot refresh, chat and notification polling, and
    /// every player-triggered action. Views call this and never touch a gateway directly, so the
    /// refresh-after-mutation rules live in exactly one place.
    /// </summary>
    public class SocialCoordinator : IDisposable
    {
        private readonly ClanSystemConfig _config;
        private readonly IAuthenticationGateway _auth;
        private readonly IFriendsGateway _friends;
        private readonly ISocialBackend _backend;
        private readonly ICommunicationService _communication;
        private readonly SocialState _state;
        private readonly List<string> _friendIdBuffer = new List<string>();

        private CancellationTokenSource _lifetimeSource;
        private bool _isDisposed;

        public SocialCoordinator(
            ClanSystemConfig config,
            IAuthenticationGateway auth,
            IFriendsGateway friends,
            ISocialBackend backend,
            ICommunicationService communication)
        {
            _config = config != null ? config : throw new ArgumentNullException(nameof(config));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _friends = friends ?? throw new ArgumentNullException(nameof(friends));
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _communication = communication;
            _state = new SocialState();
            _lifetimeSource = new CancellationTokenSource();

            if (_communication != null)
            {
                // Channel membership must track clan membership exactly: joining, leaving, being
                // kicked and switching clans all flow through this one hook.
                _state.ClanMembershipChanged += ClanMembershipChangedCallback;
            }
        }

        public SocialState State => _state;
        public ICommunicationService Communication => _communication;
        public IFriendsGateway Friends => _friends;
        public ClanSystemConfig Config => _config;
        public string PlayerId => _auth.PlayerId;
        public string PlayerName => _auth.PlayerName;
        public bool IsSignedIn => _auth.IsSignedIn;
        public bool IsFriendsReady => _friends.IsReady;

        public event Action<string, bool> StatusReported;
        public event Action SignedIn;

        public CancellationToken Lifetime => _lifetimeSource.Token;

        /// <summary>
        /// Signs in, loads the first snapshot and starts the background loops. Safe to call again
        /// after a failure: nothing is started twice.
        /// </summary>
        public async Task<SocialResult> StartAsync(string profileName)
        {
            SocialResult initialize = await _auth.InitializeAsync(profileName, Lifetime);
            if (!initialize.IsSuccess)
            {
                Report(initialize.Message, true);
                return initialize;
            }

            SocialResult signIn = await _auth.SignInAsync(Lifetime);
            if (!signIn.IsSuccess)
            {
                Report(signIn.Message, true);
                return signIn;
            }

            SignedIn?.Invoke();

            // Friends is optional for the clan flow, so a failure here degrades the UI instead of
            // blocking sign-in.
            SocialResult friendsResult = await _friends.InitializeAsync(Lifetime);
            if (friendsResult.IsSuccess)
            {
                _friends.RelationshipsChanged += RelationshipsChangedCallback;
                _friends.ClanInviteMessageReceived += ClanInviteMessageReceivedCallback;
                await _friends.SetOnlineAsync("In the social demo", Lifetime);
            }
            else
            {
                Report(friendsResult.Message, true);
            }

            SocialResult refresh = await RefreshSnapshotAsync();

            // Voice/text transport comes up after the snapshot so the clan channel is known.
            if (_communication != null)
            {
                SocialResult voice = await _communication.LoginAsync(_auth.PlayerId, _auth.PlayerName, Lifetime);
                if (voice.IsSuccess)
                {
                    string clanId = _state.Clan != null ? _state.Clan.ClanId : null;
                    await _communication.SyncClanChannelAsync(clanId, Lifetime);
                }
                else
                {
                    Report(voice.Message, true);
                }
            }

            StartLoops();
            return refresh;
        }

        public async Task<SocialResult> RefreshSnapshotAsync()
        {
            SocialResult<SocialSnapshot> result = await _backend.GetSnapshotAsync(Lifetime);
            if (!result.IsSuccess)
            {
                return SocialResult.Failure(result.Error, result.Message);
            }

            _state.ApplySnapshot(result.Value);
            await RefreshFriendsAsync();
            return SocialResult.Success();
        }

        /// <summary>
        /// Merges Friends service relationships with the clan data owned by Cloud Code.
        /// </summary>
        public async Task RefreshFriendsAsync()
        {
            if (!_friends.IsReady)
            {
                return;
            }

            List<FriendEntry> merged = new List<FriendEntry>();
            AppendRange(merged, _friends.GetFriends());
            AppendRange(merged, _friends.GetIncomingRequests());
            AppendRange(merged, _friends.GetOutgoingRequests());

            _friendIdBuffer.Clear();
            for (int i = 0; i < merged.Count; i++)
            {
                _friendIdBuffer.Add(merged[i].PlayerId);
            }

            if (_friendIdBuffer.Count > 0)
            {
                SocialResult<List<PlayerSocialInfo>> info = await _backend.GetPlayersInfoAsync(_friendIdBuffer, Lifetime);
                if (info.IsSuccess && info.Value != null)
                {
                    for (int i = 0; i < info.Value.Count; i++)
                    {
                        PlayerSocialInfo player = info.Value[i];
                        for (int j = 0; j < merged.Count; j++)
                        {
                            if (!string.Equals(merged[j].PlayerId, player.PlayerId, StringComparison.Ordinal))
                            {
                                continue;
                            }

                            merged[j].ClanId = player.ClanId;
                            merged[j].ClanName = player.ClanName;
                            merged[j].ClanTag = player.ClanTag;
                            merged[j].Score = player.Score;
                            if (string.IsNullOrEmpty(merged[j].Name))
                            {
                                merged[j].Name = player.Name;
                            }
                        }
                    }
                }
            }

            _state.SetFriends(merged);
        }

        public async Task<SocialResult> CreateClanAsync(string name, string tag, string description, bool isPublic, int emblemId)
        {
            SocialResult<ClanProfile> result = await _backend.CreateClanAsync(name, tag, description, isPublic, emblemId, Lifetime);
            if (!result.IsSuccess)
            {
                Report(result.Message, true);
                return SocialResult.Failure(result.Error, result.Message);
            }

            Report($"Clan [{result.Value.Tag}] {result.Value.Name} created.", false);
            await RefreshSnapshotAsync();
            return SocialResult.Success();
        }

        public async Task<SocialResult> JoinClanAsync(string clanId)
        {
            SocialResult<ClanProfile> result = await _backend.JoinClanAsync(clanId, Lifetime);
            if (!result.IsSuccess)
            {
                if (result.Error == SocialErrorCode.ClanIsPrivate)
                {
                    return await RequestJoinAsync(clanId, string.Empty);
                }

                Report(result.Message, true);
                return SocialResult.Failure(result.Error, result.Message);
            }

            Report($"Joined {result.Value.Name}.", false);
            await RefreshSnapshotAsync();
            return SocialResult.Success();
        }

        public async Task<SocialResult> RequestJoinAsync(string clanId, string message)
        {
            SocialResult result = await _backend.RequestJoinAsync(clanId, message, Lifetime);
            Report(result.IsSuccess ? "Join request sent." : result.Message, !result.IsSuccess);
            return result;
        }

        public async Task<SocialResult> HandleJoinRequestAsync(string playerId, bool accept)
        {
            SocialResult result = await _backend.HandleJoinRequestAsync(playerId, accept, Lifetime);
            if (result.IsSuccess)
            {
                Report(accept ? "Request accepted." : "Request declined.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public async Task<SocialResult> InvitePlayerAsync(string targetPlayerId, string targetName)
        {
            SocialResult result = await _backend.InvitePlayerAsync(targetPlayerId, Lifetime);
            if (!result.IsSuccess)
            {
                Report(result.Message, true);
                return result;
            }

            if (_friends.IsReady && _state.Clan != null)
            {
                await _friends.NotifyClanInviteAsync(targetPlayerId, _state.Clan.Name, Lifetime);
            }

            Report($"Invite sent to {targetName ?? "player"}.", false);
            return result;
        }

        public async Task<SocialResult> RespondToInviteAsync(string inviteId, bool accept)
        {
            SocialResult result = await _backend.RespondToInviteAsync(inviteId, accept, Lifetime);
            if (result.IsSuccess)
            {
                Report(accept ? "Invitation accepted." : "Invitation declined.", false);
            }
            else
            {
                Report(result.Message, true);
            }

            await RefreshSnapshotAsync();
            return result;
        }

        public async Task<SocialResult> LeaveClanAsync()
        {
            SocialResult result = await _backend.LeaveClanAsync(Lifetime);
            if (result.IsSuccess)
            {
                Report("You left the clan.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public async Task<SocialResult> KickMemberAsync(string playerId, string playerName)
        {
            SocialResult result = await _backend.KickMemberAsync(playerId, Lifetime);
            if (result.IsSuccess)
            {
                Report($"{playerName ?? "Member"} was removed.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public async Task<SocialResult> SetMemberRoleAsync(string playerId, ClanRole role)
        {
            SocialResult result = await _backend.SetMemberRoleAsync(playerId, role, Lifetime);
            if (result.IsSuccess)
            {
                Report($"Role updated to {role}.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public async Task<SocialResult> TransferOwnershipAsync(string playerId, string playerName)
        {
            SocialResult result = await _backend.TransferOwnershipAsync(playerId, Lifetime);
            if (result.IsSuccess)
            {
                Report($"{playerName ?? "Member"} is now the clan leader.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public async Task<SocialResult> UpdateClanSettingsAsync(string description, string motd, bool? isPublic, int? emblemId)
        {
            SocialResult<ClanProfile> result = await _backend.UpdateClanSettingsAsync(description, motd, isPublic, emblemId, Lifetime);
            if (!result.IsSuccess)
            {
                Report(result.Message, true);
                return SocialResult.Failure(result.Error, result.Message);
            }

            Report("Clan settings saved.", false);
            await RefreshSnapshotAsync();
            return SocialResult.Success();
        }

        public async Task<SocialResult> DisbandClanAsync()
        {
            SocialResult result = await _backend.DisbandClanAsync(Lifetime);
            if (result.IsSuccess)
            {
                Report("Clan disbanded.", false);
                await RefreshSnapshotAsync();
            }
            else
            {
                Report(result.Message, true);
            }

            return result;
        }

        public Task<SocialResult<ClanSearchPage>> SearchClansAsync(string query, int offset)
        {
            return _backend.SearchClansAsync(query, true, offset, _config.ClanSearchPageSize, Lifetime);
        }

        public Task<SocialResult<List<ClanActivityEntry>>> GetActivityAsync()
        {
            return _backend.GetActivityAsync(25, Lifetime);
        }

        public Task<SocialResult<LeaderboardPage>> GetPlayerLeaderboardAsync(int offset)
        {
            return _backend.GetPlayerLeaderboardAsync(offset, _config.LeaderboardPageSize, Lifetime);
        }

        public Task<SocialResult<LeaderboardPage>> GetClanLeaderboardAsync(int offset)
        {
            return _backend.GetClanLeaderboardAsync(offset, _config.LeaderboardPageSize, Lifetime);
        }

        public async Task<SocialResult> SubmitDemoScoreAsync()
        {
            SocialResult<ScoreSubmitResponse> result = await _backend.SubmitScoreAsync(_config.DemoScoreDelta, Lifetime);
            if (!result.IsSuccess)
            {
                Report(result.Message, true);
                return SocialResult.Failure(result.Error, result.Message);
            }

            Report($"+{result.Value.AcceptedDelta} score (total {result.Value.PlayerScore}).", false);
            await RefreshSnapshotAsync();
            return SocialResult.Success();
        }

        public async Task<SocialResult> SetPlayerNameAsync(string name)
        {
            SocialResult<string> result = await _auth.SetPlayerNameAsync(name, Lifetime);
            if (!result.IsSuccess)
            {
                Report(result.Message, true);
                return SocialResult.Failure(result.Error, result.Message);
            }

            Report($"Name set to {result.Value}.", false);

            // A rename must reach the clan roster too; the server re-reads the name service on
            // the next command, so a cheap refresh is enough to propagate it.
            await RefreshSnapshotAsync();
            return SocialResult.Success();
        }

        public Task<SocialResult<ClanDetail>> GetClanAsync(string clanId)
        {
            return _backend.GetClanAsync(clanId, Lifetime);
        }

        public Task<SocialResult<List<PlayerSocialInfo>>> GetPlayersInfoAsync(IReadOnlyList<string> playerIds)
        {
            return _backend.GetPlayersInfoAsync(playerIds, Lifetime);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _friends.RelationshipsChanged -= RelationshipsChangedCallback;
            _friends.ClanInviteMessageReceived -= ClanInviteMessageReceivedCallback;
            _state.ClanMembershipChanged -= ClanMembershipChangedCallback;
            _communication?.Dispose();

            if (_lifetimeSource != null)
            {
                _lifetimeSource.Cancel();
                _lifetimeSource.Dispose();
                _lifetimeSource = null;
            }
        }

        private void StartLoops()
        {
            _ = RunNotificationLoopAsync();
        }

        private async Task RunNotificationLoopAsync()
        {
            TimeSpan interval = TimeSpan.FromSeconds(Mathf.Max(5f, _config.NotificationPollIntervalSeconds));
            while (!_isDisposed && !Lifetime.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, Lifetime);
                    await RefreshSnapshotAsync();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    Debug.LogWarning($"[ClanSystem] Notification poll failed: {exception.Message}");
                }
            }
        }

        private static void AppendRange(List<FriendEntry> target, IReadOnlyList<FriendEntry> source)
        {
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                target.Add(source[i]);
            }
        }

        private void Report(string message, bool isError)
        {
            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            StatusReported?.Invoke(message, isError);
        }

        /// <summary>
        /// The server has changed this player's clan. Move them off the old clan channel and onto
        /// the new one; a player with no clan is left with global only.
        /// </summary>
        private void ClanMembershipChangedCallback(string clanId)
        {
            if (_communication == null)
            {
                return;
            }

            _ = _communication.SyncClanChannelAsync(clanId, Lifetime);
        }

        private void RelationshipsChangedCallback()
        {
            _ = RefreshFriendsAsync();
        }

        private void ClanInviteMessageReceivedCallback(string senderId, string clanName)
        {
            Report($"You were invited to {clanName}.", false);
            _ = RefreshSnapshotAsync();
        }
    }
}
