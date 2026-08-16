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
