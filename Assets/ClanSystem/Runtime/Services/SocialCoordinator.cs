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

        private bool _isBackgroundStarted;
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

        /// <summary>
        /// Raised as <see cref="StartAsync"/> moves between stages, so the sign-in screen can name
        /// what it is waiting on. Sign-in is a chain of independent services and the slow one is
        /// not always the same, which a single "Signing in..." message cannot express.
        /// </summary>
        public event Action<string> StartupStageChanged;

        public CancellationToken Lifetime => _lifetimeSource.Token;

        /// <summary>
        /// Signs in, loads the first snapshot and starts the background loops. Safe to call again
        /// after a failure: nothing is started twice.
        /// </summary>
        public async Task<SocialResult> StartAsync(string profileName)
        {
            // Only what the window cannot render without: an authenticated player, and the first
            // snapshot. Friends and voice are deliberately not awaited here - see
            // StartBackgroundServices. Measured, those two were 12.1s of a 15.2s sign-in while the
            // window sat on a spinner that could already have been showing the player their clan.
            System.Diagnostics.Stopwatch total = System.Diagnostics.Stopwatch.StartNew();
            System.Diagnostics.Stopwatch stage = System.Diagnostics.Stopwatch.StartNew();
            System.Text.StringBuilder timings = new System.Text.StringBuilder();

            void BeginStage(string label)
            {
                stage.Restart();
                StartupStageChanged?.Invoke(label);
            }

            void EndStage(string label)
            {
                timings.Append(label).Append(' ').Append(stage.ElapsedMilliseconds).Append("ms; ");
            }

            BeginStage("Starting Unity Gaming Services...");
            SocialResult initialize = await _auth.InitializeAsync(profileName, Lifetime);
            EndStage("services");
            if (!initialize.IsSuccess)
            {
                Report(initialize.Message, true);
                return initialize;
            }

            BeginStage("Signing in...");
            SocialResult signIn = await _auth.SignInAsync(Lifetime);
            EndStage("auth");
            if (!signIn.IsSuccess)
            {
                Report(signIn.Message, true);
                return signIn;
            }

            SignedIn?.Invoke();

            BeginStage("Loading your clan...");
            SocialResult refresh = await RefreshSnapshotAsync();
            EndStage("snapshot");

            StartLoops();

            Debug.Log($"[ClanSystem] Sign-in took {total.ElapsedMilliseconds}ms - {timings}");
            StartupStageChanged?.Invoke(string.Empty);
            return refresh;
        }

        /// <summary>
        /// Brings up the services the window can render without: the friends list and the
        /// voice/text transport. Call once, after the window exists - failures here surface as
        /// status messages, which are lost if nothing is listening yet.
        ///
        /// Neither is awaited by <see cref="StartAsync"/> because neither gates the first frame.
        /// The friends tab already renders an empty list until <see cref="IFriendsGateway.IsReady"/>
        /// turns true, and the voice rail already shows a connecting state and disables its own
        /// controls until the transport reports Connected. Both then repaint from their own events.
        ///
        /// The two run concurrently rather than in sequence: they share no state, and running them
        /// one after the other would add the slower one's latency to the faster one for no reason.
        /// </summary>
        public void StartBackgroundServices()
        {
            if (_isBackgroundStarted)
            {
                return;
            }

            _isBackgroundStarted = true;
            _ = StartFriendsAsync();
            _ = StartCommunicationAsync();
        }

        private async Task StartFriendsAsync()
        {
            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // Friends is optional for the clan flow, so a failure degrades the UI rather than
                // blocking anything.
                SocialResult friendsResult = await _friends.InitializeAsync(Lifetime);
                if (!friendsResult.IsSuccess)
                {
                    Report(friendsResult.Message, true);
                    return;
                }

                _friends.RelationshipsChanged += RelationshipsChangedCallback;
                _friends.ClanInviteMessageReceived += ClanInviteMessageReceivedCallback;
                await _friends.SetOnlineAsync("In the social demo", Lifetime);
                await RefreshFriendsAsync();

                Debug.Log($"[ClanSystem] Friends ready after {stopwatch.ElapsedMilliseconds}ms.");
            }
            catch (OperationCanceledException)
            {
                // Signed out or the scene went away while connecting.
            }
        }

        private async Task StartCommunicationAsync()
        {
            if (_communication == null)
            {
                return;
            }

            System.Diagnostics.Stopwatch stopwatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                // The voice transport reaches its own service over its own connection, and that first
                // handshake times out on a cold or congested network far more often than the HTTP
                // services do. A timeout is a slow start rather than a refusal, so it is retried;
                // anything else the server actually decided is reported on the first answer.
                int attempts = Mathf.Max(1, _config.RequestRetryCount + 1);
                SocialResult voice = SocialResult.Success();
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    voice = await _communication.LoginAsync(_auth.PlayerId, _auth.PlayerName, Lifetime);
                    if (voice.IsSuccess || voice.Error != SocialErrorCode.ServiceUnavailable)
                    {
                        break;
                    }

                    if (attempt < attempts - 1)
                    {
                        await Task.Delay(1000 * (attempt + 1), Lifetime);
                    }
                }

                if (!voice.IsSuccess)
                {
                    Report(voice.Message, true);
                    return;
                }

                // Read the clan id after the login completes, not before: the snapshot may have been
                // refreshed by the notification loop while the transport was still connecting.
                string clanId = _state.Clan != null ? _state.Clan.ClanId : null;
                await _communication.SyncClanChannelAsync(clanId, Lifetime);

                Debug.Log($"[ClanSystem] Voice and chat ready after {stopwatch.ElapsedMilliseconds}ms.");
            }
            catch (OperationCanceledException)
            {
                // Signed out or the scene went away while connecting.
            }
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

            // The chat transport stamps the sender name into each message from its own session, so
            // it has to be told about the rename as well - otherwise new messages keep going out
            // under the old name even though the roster shows the new one.
            if (_communication != null)
            {
                SocialResult applied = await _communication.UpdateDisplayNameAsync(result.Value, Lifetime);
                if (!applied.IsSuccess)
                {
                    Report(applied.Message, true);
                }
            }

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
