using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using Unity.Services.Vivox;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Vivox-backed text and voice. Both channels (global, clan) carry text and audio through the
    /// same transport, so chat history, presence and speaking state all come from one source.
    ///
    /// Channel names are derived here for convenience only. Authorisation happens server-side: every
    /// join needs a token minted by <c>SOCIAL_VivoxToken.js</c>, which rebuilds the permitted clan
    /// channel from the caller's real membership. A tampered clan id yields no token and no join.
    /// </summary>
    public class VivoxCommunicationService : ICommunicationService
    {
        private const int _historyRequestMax = 50;

        private readonly ClanSystemConfig _config;
        private readonly CloudCodeVivoxTokenProvider _tokenProvider;

        private readonly List<CommMessage> _globalMessages = new List<CommMessage>();
        private readonly List<CommMessage> _clanMessages = new List<CommMessage>();
        private readonly HashSet<string> _mutedPlayers = new HashSet<string>();
        private readonly HashSet<string> _voiceChannels = new HashSet<string>();
        private readonly HashSet<string> _textChannels = new HashSet<string>();
        private readonly Dictionary<string, Task<SocialResult>> _joinsInFlight = new Dictionary<string, Task<SocialResult>>();

        private string _clanChannelName;
        private bool _isDisposed;

        public VivoxCommunicationService(ClanSystemConfig config, CloudCodeVivoxTokenProvider tokenProvider)
        {
            _config = config != null ? config : throw new ArgumentNullException(nameof(config));
            _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        }

        public CommConnectionState State { get; private set; } = CommConnectionState.Disconnected;
        public string StateDetail { get; private set; } = string.Empty;

        public bool IsLoggedIn => VivoxService.Instance != null && VivoxService.Instance.IsLoggedIn;
        public bool IsMicrophoneMuted { get; private set; }
        public bool IsSpeakerMuted { get; private set; }
        public CommChannelKind? ActiveVoiceChannel { get; private set; }

        public event Action StateChanged;
        public event Action<CommMessage> MessageReceived;
        public event Action<CommChannelKind> ParticipantsChanged;

        /// <summary>
        /// True only for the channel the microphone currently transmits into. Every joined channel
        /// carries audio, so "in voice" means transmitting, not merely present.
        /// </summary>
        public bool IsVoiceJoined(CommChannelKind channel)
        {
            return ActiveVoiceChannel.HasValue && ActiveVoiceChannel.Value == channel;
        }

        public bool IsTextJoined(CommChannelKind channel)
        {
            string name = ResolveChannelName(channel);
            return !string.IsNullOrEmpty(name) && _textChannels.Contains(name);
        }

        public async Task<SocialResult> LoginAsync(string playerId, string displayName, CancellationToken cancellationToken)
        {
            SetState(CommConnectionState.Connecting, "Connecting to voice service...");

            try
            {
                await VivoxService.Instance.InitializeAsync();
                cancellationToken.ThrowIfCancellationRequested();

                // Must be registered before login so even the login token is server-issued.
                VivoxService.Instance.SetTokenProvider(_tokenProvider);

                HookEvents();

                LoginOptions options = new LoginOptions
                {
                    PlayerId = playerId,
                    DisplayName = string.IsNullOrEmpty(displayName) ? playerId : displayName,
                    ParticipantUpdateFrequency = ParticipantPropertyUpdateFrequency.FivePerSecond,
                };

                await VivoxService.Instance.LoginAsync(options);
                cancellationToken.ThrowIfCancellationRequested();

                IsMicrophoneMuted = _config.IsJoinVoiceMutedByDefault;
                if (IsMicrophoneMuted)
                {
                    VivoxService.Instance.MuteInputDevice();
                }
                else
                {
                    VivoxService.Instance.UnmuteInputDevice();
                }

                SetState(CommConnectionState.Connected, "Connected");

                if (_config.IsAutoJoinGlobalTextOnLogin)
                {
                    await JoinTextAsync(CommChannelKind.Global, cancellationToken);
                }

                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                SetState(CommConnectionState.Disconnected, "Cancelled");
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                // A refused token surfaces here as a login failure; report the server's reason.
                string detail = !string.IsNullOrEmpty(_tokenProvider.LastError)
                    ? _tokenProvider.LastError
                    : exception.Message;

                bool isNotConfigured = _tokenProvider.LastErrorCode == SocialErrorCode.Unknown
                    && !string.IsNullOrEmpty(_tokenProvider.LastError);

                SetState(isNotConfigured ? CommConnectionState.NotConfigured : CommConnectionState.Failed, detail);
                Debug.LogWarning($"[ClanSystem] Vivox login failed: {detail}");
                return SocialResult.Failure(SocialErrorCode.ServiceUnavailable, detail);
            }
        }

        public async Task LogoutAsync()
        {
            try
            {
                if (VivoxService.Instance != null && VivoxService.Instance.IsLoggedIn)
                {
                    await VivoxService.Instance.LeaveAllChannelsAsync();
                    await VivoxService.Instance.LogoutAsync();
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Vivox logout failed: {exception.Message}");
            }
            finally
            {
                _voiceChannels.Clear();
                _textChannels.Clear();
                ActiveVoiceChannel = null;
                SetState(CommConnectionState.Disconnected, "Signed out");
            }
        }

        /// <summary>
        /// Keeps channel membership honest: when the player's clan changes - joined, left, kicked or
        /// switched - the previous clan channel is left and its cached history dropped.
        /// </summary>
        public async Task SyncClanChannelAsync(string clanId, CancellationToken cancellationToken)
        {
            string desired = string.IsNullOrEmpty(clanId) ? null : _config.BuildClanChannelName(clanId);
            if (string.Equals(desired, _clanChannelName, StringComparison.Ordinal))
            {
                return;
            }

            string previous = _clanChannelName;
            _clanChannelName = desired;

            if (!string.IsNullOrEmpty(previous))
            {
                await LeaveChannelAsync(previous);
                _clanMessages.Clear();
                MessageReceived?.Invoke(null);
                ParticipantsChanged?.Invoke(CommChannelKind.Clan);
            }

            if (!string.IsNullOrEmpty(desired) && IsLoggedIn)
            {
                await JoinTextAsync(CommChannelKind.Clan, cancellationToken);
            }
        }

        public Task<SocialResult> JoinTextAsync(CommChannelKind channel, CancellationToken cancellationToken)
        {
            return JoinChannelAsync(channel, ChatCapability.TextAndAudio, cancellationToken);
        }

        /// <summary>
        /// Starts transmitting into a channel. Channels are joined once with audio enabled and the
        /// microphone simply routed between them - Vivox cannot change a joined channel's capability
        /// in place, and leaving to rejoin corrupts its channel table.
        /// </summary>
        public async Task<SocialResult> JoinVoiceAsync(CommChannelKind channel, CancellationToken cancellationToken)
        {
            string name = ResolveChannelName(channel);
            if (string.IsNullOrEmpty(name))
            {
                return SocialResult.Failure(SocialErrorCode.NotInClan, "You are not in a clan.");
            }

            // Only join when not already present; switching which channel carries the microphone
            // must not depend on a join succeeding.
            if (!_textChannels.Contains(name))
            {
                SocialResult joined = await JoinChannelAsync(channel, ChatCapability.TextAndAudio, cancellationToken);
                if (!joined.IsSuccess)
                {
                    return joined;
                }
            }

            try
            {
                await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.Single, name);
                ActiveVoiceChannel = channel;

                // The microphone is left exactly as the player set it. Joining a voice channel must
                // never open a live mic on its own.
                RaiseStateChanged();
                return SocialResult.Success();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Could not transmit into '{name}': {exception.Message}");
                return SocialResult.Failure(SocialErrorCode.ServiceUnavailable, "Could not open the microphone for that channel.");
            }
        }

        /// <summary>
        /// Stops transmitting while staying in the channel, so the player keeps hearing others and
        /// keeps reading its chat.
        /// </summary>
        public async Task<SocialResult> LeaveVoiceAsync(CommChannelKind channel, CancellationToken cancellationToken)
        {
            if (ResolveChannelName(channel) == null)
            {
                return SocialResult.Success();
            }

            try
            {
                await VivoxService.Instance.SetChannelTransmissionModeAsync(TransmissionMode.None);
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Could not stop transmitting: {exception.Message}");
            }

            if (ActiveVoiceChannel == channel)
            {
                ActiveVoiceChannel = null;
            }

            // Nothing is transmitting now, so mute the input as a visible safety net.
            SetMicrophoneMuted(true);
            RaiseStateChanged();
            return SocialResult.Success();
        }

        public async Task<SocialResult> SendTextAsync(CommChannelKind channel, string text, CancellationToken cancellationToken)
        {
            string name = ResolveChannelName(channel);
            if (string.IsNullOrEmpty(name))
            {
                return SocialResult.Failure(SocialErrorCode.NotInClan, "Join a clan to use clan chat.");
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return SocialResult.Failure(SocialErrorCode.EmptyMessage, "Message is empty.");
            }

            if (!_textChannels.Contains(name) && !_voiceChannels.Contains(name))
            {
                SocialResult join = await JoinTextAsync(channel, cancellationToken);
                if (!join.IsSuccess)
                {
                    return join;
                }
            }

            try
            {
                await VivoxService.Instance.SendChannelTextMessageAsync(name, text);
                return SocialResult.Success();
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Chat send failed: {exception.Message}");
                return SocialResult.Failure(SocialErrorCode.ServiceUnavailable, "Message could not be sent.");
            }
        }

        public async Task<SocialResult<List<CommMessage>>> GetHistoryAsync(CommChannelKind channel, int count, CancellationToken cancellationToken)
        {
            string name = ResolveChannelName(channel);
            if (string.IsNullOrEmpty(name))
            {
                return SocialResult<List<CommMessage>>.Success(new List<CommMessage>());
            }

            // History is only meaningful once the channel is joined; asking earlier throws inside
            // the SDK, so an empty result is the honest answer.
            if (!_textChannels.Contains(name) && !_voiceChannels.Contains(name))
            {
                return SocialResult<List<CommMessage>>.Success(new List<CommMessage>());
            }

            try
            {
                ReadOnlyCollection<VivoxMessage> history = await VivoxService.Instance.GetChannelTextMessageHistoryAsync(
                    name, Mathf.Clamp(count, 1, _historyRequestMax));

                if (history == null)
                {
                    return SocialResult<List<CommMessage>>.Success(new List<CommMessage>());
                }

                List<CommMessage> buffer = channel == CommChannelKind.Clan ? _clanMessages : _globalMessages;
                buffer.Clear();
                for (int i = 0; i < history.Count; i++)
                {
                    buffer.Add(Convert(history[i], channel));
                }

                buffer.Sort((left, right) => left.TimestampUtc.CompareTo(right.TimestampUtc));
                return SocialResult<List<CommMessage>>.Success(new List<CommMessage>(buffer));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Chat history failed: {exception.Message}");
                return SocialResult<List<CommMessage>>.Failure(SocialErrorCode.ServiceUnavailable, "Could not load history.");
            }
        }

        public IReadOnlyList<CommMessage> GetBufferedMessages(CommChannelKind channel)
        {
            return channel == CommChannelKind.Clan ? _clanMessages : _globalMessages;
        }

        public IReadOnlyList<CommParticipant> GetParticipants(CommChannelKind channel)
        {
            List<CommParticipant> result = new List<CommParticipant>();
            string name = ResolveChannelName(channel);
            if (string.IsNullOrEmpty(name) || VivoxService.Instance == null)
            {
                return result;
            }

            ReadOnlyDictionary<string, ReadOnlyCollection<VivoxParticipant>> channels = VivoxService.Instance.ActiveChannels;
            ReadOnlyCollection<VivoxParticipant> participants;
            if (channels == null || !channels.TryGetValue(name, out participants))
            {
                return result;
            }

            for (int i = 0; i < participants.Count; i++)
            {
                VivoxParticipant participant = participants[i];
                result.Add(new CommParticipant
                {
                    PlayerId = participant.PlayerId,
                    DisplayName = participant.DisplayName,
                    ChannelName = name,
                    IsSelf = participant.IsSelf,
                    IsSpeaking = participant.SpeechDetected,
                    IsMuted = participant.IsMuted,
                    IsInAudio = participant.IsInAudio,
                    LocalVolume = participant.LocalVolume,
                });
            }

            return result;
        }

        public void SetMicrophoneMuted(bool isMuted)
        {
            if (VivoxService.Instance == null)
            {
                return;
            }

            IsMicrophoneMuted = isMuted;
            if (isMuted)
            {
                VivoxService.Instance.MuteInputDevice();
            }
            else
            {
                VivoxService.Instance.UnmuteInputDevice();
            }

            RaiseStateChanged();
        }

        public void SetSpeakerMuted(bool isMuted)
        {
            if (VivoxService.Instance == null)
            {
                return;
            }

            IsSpeakerMuted = isMuted;
            if (isMuted)
            {
                VivoxService.Instance.MuteOutputDevice();
            }
            else
            {
                VivoxService.Instance.UnmuteOutputDevice();
            }

            RaiseStateChanged();
        }

        public void SetPlayerMuted(string playerId, bool isMuted)
        {
            VivoxParticipant participant = FindParticipant(playerId);
            if (participant == null)
            {
                return;
            }

            if (isMuted)
            {
                participant.MutePlayerLocally();
                _mutedPlayers.Add(playerId);
            }
            else
            {
                participant.UnmutePlayerLocally();
                _mutedPlayers.Remove(playerId);
            }

            ParticipantsChanged?.Invoke(CommChannelKind.Global);
            ParticipantsChanged?.Invoke(CommChannelKind.Clan);
        }

        public void SetPlayerVolume(string playerId, int volume)
        {
            VivoxParticipant participant = FindParticipant(playerId);
            participant?.SetLocalVolume(Mathf.Clamp(volume, -50, 50));
        }

        public bool IsPlayerMuted(string playerId)
        {
            return !string.IsNullOrEmpty(playerId) && _mutedPlayers.Contains(playerId);
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            UnhookEvents();
            _ = LogoutAsync();
        }

        /// <summary>
        /// Joins a channel, collapsing concurrent requests for the same channel onto a single join.
        /// Two overlapping joins leave the Vivox SDK with a half-built session, and a caller that
        /// merely arrived second still wants the channel, so it awaits the join already running
        /// rather than failing.
        /// </summary>
        private Task<SocialResult> JoinChannelAsync(CommChannelKind channel, ChatCapability capability, CancellationToken cancellationToken)
        {
            string name = ResolveChannelName(channel);
            if (string.IsNullOrEmpty(name))
            {
                return Task.FromResult(SocialResult.Failure(SocialErrorCode.NotInClan, "You are not in a clan."));
            }

            if (!IsLoggedIn)
            {
                return Task.FromResult(SocialResult.Failure(SocialErrorCode.ServiceUnavailable, "Voice service is not connected."));
            }

            Task<SocialResult> pending;
            if (_joinsInFlight.TryGetValue(name, out pending))
            {
                return pending;
            }

            Task<SocialResult> join = JoinChannelCoreAsync(channel, name, cancellationToken);
            _joinsInFlight[name] = join;
            return join;
        }

        private async Task<SocialResult> JoinChannelCoreAsync(CommChannelKind channel, string name, CancellationToken cancellationToken)
        {
            try
            {
                // Joining an already-joined channel is what Vivox rejects as a duplicate, so a
                // second join is a no-op rather than an error.
                if (_textChannels.Contains(name) || IsChannelLiveInSdk(name))
                {
                    _textChannels.Add(name);
                    return SocialResult.Success();
                }

                // Always joined with audio available; whether the microphone actually transmits
                // here is controlled separately by the transmission mode.
                ChannelOptions options = new ChannelOptions { MakeActiveChannelUponJoining = false };
                await VivoxService.Instance.JoinGroupChannelAsync(name, ChatCapability.TextAndAudio, options);
                cancellationToken.ThrowIfCancellationRequested();

                _textChannels.Add(name);
                _voiceChannels.Add(name);

                RaiseStateChanged();
                ParticipantsChanged?.Invoke(channel);
                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                // The most common cause is the server refusing to mint a token for this channel.
                string detail = !string.IsNullOrEmpty(_tokenProvider.LastError)
                    ? _tokenProvider.LastError
                    : exception.Message;

                Debug.LogWarning($"[ClanSystem] Join '{name}' failed: {detail}");
                SocialErrorCode code = _tokenProvider.LastErrorCode != SocialErrorCode.None
                    ? _tokenProvider.LastErrorCode
                    : SocialErrorCode.ServiceUnavailable;
                return SocialResult.Failure(code, detail);
            }
            finally
            {
                _joinsInFlight.Remove(name);
            }
        }

        /// <summary>
        /// True while the SDK still holds a session for this channel. Leaving is asynchronous, so
        /// the session can outlive the call that requested it.
        /// </summary>
        private bool IsChannelLiveInSdk(string channelName)
        {
            if (VivoxService.Instance == null)
            {
                return false;
            }

            ReadOnlyDictionary<string, ReadOnlyCollection<VivoxParticipant>> channels = VivoxService.Instance.ActiveChannels;
            return channels != null && channels.ContainsKey(channelName);
        }

        private async Task LeaveChannelAsync(string channelName)
        {
            try
            {
                if (VivoxService.Instance != null && VivoxService.Instance.IsLoggedIn)
                {
                    await VivoxService.Instance.LeaveChannelAsync(channelName);
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Leave '{channelName}' failed: {exception.Message}");
            }
            finally
            {
                _voiceChannels.Remove(channelName);
                _textChannels.Remove(channelName);
            }
        }

        private string ResolveChannelName(CommChannelKind channel)
        {
            return channel == CommChannelKind.Clan ? _clanChannelName : _config.GlobalChannelName;
        }

        private CommChannelKind? ResolveChannelKind(string channelName)
        {
            if (string.Equals(channelName, _config.GlobalChannelName, StringComparison.Ordinal))
            {
                return CommChannelKind.Global;
            }

            if (!string.IsNullOrEmpty(_clanChannelName) && string.Equals(channelName, _clanChannelName, StringComparison.Ordinal))
            {
                return CommChannelKind.Clan;
            }

            return null;
        }

        private CommMessage Convert(VivoxMessage message, CommChannelKind channel)
        {
            return new CommMessage
            {
                Id = message.MessageId,
                SenderId = message.SenderPlayerId,
                SenderName = string.IsNullOrEmpty(message.SenderDisplayName) ? message.SenderPlayerId : message.SenderDisplayName,
                Text = message.MessageText,
                TimestampUtc = message.ReceivedTime.ToUniversalTime(),
                Channel = channel,
                IsFromSelf = message.FromSelf,
            };
        }

        private VivoxParticipant FindParticipant(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || VivoxService.Instance == null)
            {
                return null;
            }

            ReadOnlyDictionary<string, ReadOnlyCollection<VivoxParticipant>> channels = VivoxService.Instance.ActiveChannels;
            if (channels == null)
            {
                return null;
            }

            foreach (KeyValuePair<string, ReadOnlyCollection<VivoxParticipant>> pair in channels)
            {
                for (int i = 0; i < pair.Value.Count; i++)
                {
                    if (string.Equals(pair.Value[i].PlayerId, playerId, StringComparison.Ordinal))
                    {
                        return pair.Value[i];
                    }
                }
            }

            return null;
        }

        private void HookEvents()
        {
            UnhookEvents();
            VivoxService.Instance.ChannelMessageReceived += ChannelMessageReceivedCallback;
            VivoxService.Instance.ParticipantAddedToChannel += ParticipantAddedCallback;
            VivoxService.Instance.ParticipantRemovedFromChannel += ParticipantRemovedCallback;
            VivoxService.Instance.ConnectionRecovering += ConnectionRecoveringCallback;
            VivoxService.Instance.ConnectionRecovered += ConnectionRecoveredCallback;
            VivoxService.Instance.ConnectionFailedToRecover += ConnectionFailedToRecoverCallback;
            VivoxService.Instance.LoggedOut += LoggedOutCallback;
        }

        private void UnhookEvents()
        {
            if (VivoxService.Instance == null)
            {
                return;
            }

            VivoxService.Instance.ChannelMessageReceived -= ChannelMessageReceivedCallback;
            VivoxService.Instance.ParticipantAddedToChannel -= ParticipantAddedCallback;
            VivoxService.Instance.ParticipantRemovedFromChannel -= ParticipantRemovedCallback;
            VivoxService.Instance.ConnectionRecovering -= ConnectionRecoveringCallback;
            VivoxService.Instance.ConnectionRecovered -= ConnectionRecoveredCallback;
            VivoxService.Instance.ConnectionFailedToRecover -= ConnectionFailedToRecoverCallback;
            VivoxService.Instance.LoggedOut -= LoggedOutCallback;
        }

        private void ChannelMessageReceivedCallback(VivoxMessage message)
        {
            CommChannelKind? kind = ResolveChannelKind(message.ChannelName);
            if (!kind.HasValue)
            {
                return;
            }

            CommMessage converted = Convert(message, kind.Value);
            List<CommMessage> buffer = kind.Value == CommChannelKind.Clan ? _clanMessages : _globalMessages;
            buffer.Add(converted);

            int overflow = buffer.Count - _config.ChatHistoryLimit;
            if (overflow > 0)
            {
                buffer.RemoveRange(0, overflow);
            }

            MessageReceived?.Invoke(converted);
        }

        private void ParticipantAddedCallback(VivoxParticipant participant)
        {
            // Re-apply local mutes so a player muted earlier stays muted when they rejoin.
            if (_mutedPlayers.Contains(participant.PlayerId))
            {
                participant.MutePlayerLocally();
            }

            participant.ParticipantSpeechDetected += () => RaiseParticipants(participant.ChannelName);
            participant.ParticipantMuteStateChanged += () => RaiseParticipants(participant.ChannelName);
            RaiseParticipants(participant.ChannelName);
        }

        private void ParticipantRemovedCallback(VivoxParticipant participant)
        {
            RaiseParticipants(participant.ChannelName);
        }

        private void RaiseParticipants(string channelName)
        {
            CommChannelKind? kind = ResolveChannelKind(channelName);
            if (kind.HasValue)
            {
                ParticipantsChanged?.Invoke(kind.Value);
            }
        }

        private void ConnectionRecoveringCallback()
        {
            SetState(CommConnectionState.Recovering, "Reconnecting...");
        }

        private void ConnectionRecoveredCallback()
        {
            SetState(CommConnectionState.Connected, "Connected");
        }

        private void ConnectionFailedToRecoverCallback()
        {
            _voiceChannels.Clear();
            _textChannels.Clear();
            ActiveVoiceChannel = null;
            SetState(CommConnectionState.Failed, "Connection lost");
        }

        private void LoggedOutCallback()
        {
            _voiceChannels.Clear();
            _textChannels.Clear();
            ActiveVoiceChannel = null;
            SetState(CommConnectionState.Disconnected, "Signed out");
        }

        private void SetState(CommConnectionState state, string detail)
        {
            State = state;
            StateDetail = detail ?? string.Empty;
            RaiseStateChanged();
        }

        private void RaiseStateChanged()
        {
            StateChanged?.Invoke();
        }
    }
}
