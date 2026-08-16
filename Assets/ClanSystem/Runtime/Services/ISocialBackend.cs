using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;

namespace ClanSystem.Services
{
    /// <summary>
    /// Every server-authoritative social operation, in one seam. The Cloud Code implementation is
    /// the production one; the interface exists so UI and coordinator logic can be tested against
    /// a fake without a live project.
    /// </summary>
    public interface ISocialBackend
    {
        Task<SocialResult<SocialSnapshot>> GetSnapshotAsync(CancellationToken cancellationToken);

        Task<SocialResult<ClanDetail>> GetClanAsync(string clanId, CancellationToken cancellationToken);

        Task<SocialResult<ClanSearchPage>> SearchClansAsync(string query, bool onlyPublic, int offset, int limit, CancellationToken cancellationToken);

        Task<SocialResult<List<ClanMember>>> GetMembersAsync(CancellationToken cancellationToken);

        Task<SocialResult<List<ClanActivityEntry>>> GetActivityAsync(int limit, CancellationToken cancellationToken);

        Task<SocialResult<List<ClanJoinRequest>>> GetJoinRequestsAsync(CancellationToken cancellationToken);

        Task<SocialResult<List<PlayerSocialInfo>>> GetPlayersInfoAsync(IReadOnlyList<string> playerIds, CancellationToken cancellationToken);

        Task<SocialResult<ClanProfile>> CreateClanAsync(string name, string tag, string description, bool isPublic, int emblemId, CancellationToken cancellationToken);

        Task<SocialResult<ClanProfile>> JoinClanAsync(string clanId, CancellationToken cancellationToken);

        Task<SocialResult> RequestJoinAsync(string clanId, string message, CancellationToken cancellationToken);

        Task<SocialResult> HandleJoinRequestAsync(string playerId, bool accept, CancellationToken cancellationToken);

        Task<SocialResult> InvitePlayerAsync(string targetPlayerId, CancellationToken cancellationToken);

        Task<SocialResult> RespondToInviteAsync(string inviteId, bool accept, CancellationToken cancellationToken);

        Task<SocialResult> LeaveClanAsync(CancellationToken cancellationToken);

        Task<SocialResult> KickMemberAsync(string targetPlayerId, CancellationToken cancellationToken);

        Task<SocialResult> SetMemberRoleAsync(string targetPlayerId, ClanRole role, CancellationToken cancellationToken);

        Task<SocialResult> TransferOwnershipAsync(string targetPlayerId, CancellationToken cancellationToken);

        Task<SocialResult<ClanProfile>> UpdateClanSettingsAsync(string description, string motd, bool? isPublic, int? emblemId, CancellationToken cancellationToken);

        Task<SocialResult> DisbandClanAsync(CancellationToken cancellationToken);



        Task<SocialResult<LeaderboardPage>> GetPlayerLeaderboardAsync(int offset, int limit, CancellationToken cancellationToken);

        Task<SocialResult<LeaderboardPage>> GetClanLeaderboardAsync(int offset, int limit, CancellationToken cancellationToken);

        Task<SocialResult<ScoreSubmitResponse>> SubmitScoreAsync(long delta, CancellationToken cancellationToken);
    }
}
