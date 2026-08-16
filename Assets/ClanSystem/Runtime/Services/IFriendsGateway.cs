using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;

namespace ClanSystem.Services
{
    /// <summary>
    /// Friend relationships and presence. Backed by the Friends service, which owns the social
    /// graph; clan affiliation is merged in from Cloud Code by the coordinator.
    /// </summary>
    public interface IFriendsGateway
    {
        bool IsReady { get; }

        event Action RelationshipsChanged;
        event Action<string, string> ClanInviteMessageReceived;

        Task<SocialResult> InitializeAsync(CancellationToken cancellationToken);

        IReadOnlyList<FriendEntry> GetFriends();

        IReadOnlyList<FriendEntry> GetIncomingRequests();

        IReadOnlyList<FriendEntry> GetOutgoingRequests();

        IReadOnlyList<FriendEntry> GetBlocked();

        Task<SocialResult> AddFriendByNameAsync(string name, CancellationToken cancellationToken);

        Task<SocialResult> AcceptFriendRequestAsync(string playerId, CancellationToken cancellationToken);

        Task<SocialResult> DeclineFriendRequestAsync(string playerId, CancellationToken cancellationToken);

        Task<SocialResult> RemoveFriendAsync(string playerId, CancellationToken cancellationToken);

        Task<SocialResult> BlockPlayerAsync(string playerId, CancellationToken cancellationToken);

        Task<SocialResult> UnblockPlayerAsync(string playerId, CancellationToken cancellationToken);

        Task<SocialResult> SetOnlineAsync(string activity, CancellationToken cancellationToken);

        Task<SocialResult> NotifyClanInviteAsync(string targetPlayerId, string clanName, CancellationToken cancellationToken);

        Task<SocialResult> RefreshAsync(CancellationToken cancellationToken);
    }
}
