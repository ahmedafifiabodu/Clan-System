using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;

namespace ClanSystem.Services
{
    /// <summary>
    /// Unified text + voice communication. One transport serves both, so there is no second chat
    /// system to keep in sync. Channel membership follows the server's view of the player's clan.
    /// </summary>
    public interface ICommunicationService : IDisposable
    {
        CommConnectionState State { get; }
        string StateDetail { get; }

        bool IsLoggedIn { get; }
        bool IsMicrophoneMuted { get; }
        bool IsSpeakerMuted { get; }

        /// <summary>
        /// The local player's own live microphone level, 0 to 1, measured by the transport. Reads 0
        /// when muted or not in a voice channel, so a level meter bound to it goes flat on mute
        /// without the view needing to special-case either state.
        /// </summary>
        float MicrophoneEnergy { get; }

        /// <summary>Channel the microphone currently transmits into, if any.</summary>
        CommChannelKind? ActiveVoiceChannel { get; }

        bool IsVoiceJoined(CommChannelKind channel);
        bool IsTextJoined(CommChannelKind channel);

        event Action StateChanged;
        event Action<CommMessage> MessageReceived;
        event Action<CommChannelKind> ParticipantsChanged;

        Task<SocialResult> LoginAsync(string playerId, string displayName, CancellationToken cancellationToken);

        Task LogoutAsync();

        /// <summary>
        /// Applies a new display name to the live session, so messages sent from now on carry it.
        /// Messages already sent keep the name they were sent under - the transport stamps the name
        /// into each message, and history is server-held.
        /// </summary>
        Task<SocialResult> UpdateDisplayNameAsync(string displayName, CancellationToken cancellationToken);

        /// <summary>
        /// Brings joined channels in line with the player's real clan: joins the new clan channel,
        /// leaves the previous one. Called whenever the server snapshot changes.
        /// </summary>
        Task SyncClanChannelAsync(string clanId, CancellationToken cancellationToken);

        Task<SocialResult> JoinTextAsync(CommChannelKind channel, CancellationToken cancellationToken);

        Task<SocialResult> JoinVoiceAsync(CommChannelKind channel, CancellationToken cancellationToken);

        Task<SocialResult> LeaveVoiceAsync(CommChannelKind channel, CancellationToken cancellationToken);

        Task<SocialResult> SendTextAsync(CommChannelKind channel, string text, CancellationToken cancellationToken);

        Task<SocialResult<List<CommMessage>>> GetHistoryAsync(CommChannelKind channel, int count, CancellationToken cancellationToken);

        IReadOnlyList<CommMessage> GetBufferedMessages(CommChannelKind channel);

        IReadOnlyList<CommParticipant> GetParticipants(CommChannelKind channel);

        void SetMicrophoneMuted(bool isMuted);

        void SetSpeakerMuted(bool isMuted);

        void SetPlayerMuted(string playerId, bool isMuted);

        void SetPlayerVolume(string playerId, int volume);

        bool IsPlayerMuted(string playerId);
    }
}
