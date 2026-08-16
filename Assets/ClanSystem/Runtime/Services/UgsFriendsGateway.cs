using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using Unity.Services.Friends;
using Unity.Services.Friends.Models;
using Unity.Services.Friends.Notifications;
using UnityEngine;

namespace ClanSystem.Services
{
    /// <summary>
    /// Wraps the Friends service: relationship lists, presence, and the lightweight message used to
    /// push a "you were invited" nudge to an online player. The invite itself is still created and
    /// validated by Cloud Code - this message only shortens the delay before the target sees it.
    /// </summary>
    public class UgsFriendsGateway : IFriendsGateway
    {
        private const string _inviteMessagePrefix = "clan-invite:";

        private readonly List<FriendEntry> _friends = new List<FriendEntry>();
        private readonly List<FriendEntry> _incoming = new List<FriendEntry>();
        private readonly List<FriendEntry> _outgoing = new List<FriendEntry>();
        private readonly List<FriendEntry> _blocked = new List<FriendEntry>();

        private bool _isReady;

        public bool IsReady => _isReady;

        public event Action RelationshipsChanged;
        public event Action<string, string> ClanInviteMessageReceived;

        public async Task<SocialResult> InitializeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await FriendsService.Instance.InitializeAsync();
                cancellationToken.ThrowIfCancellationRequested();

                FriendsService.Instance.RelationshipAdded += RelationshipAddedCallback;
                FriendsService.Instance.RelationshipDeleted += RelationshipDeletedCallback;
                FriendsService.Instance.PresenceUpdated += PresenceUpdatedCallback;
                FriendsService.Instance.MessageReceived += MessageReceivedCallback;

                _isReady = true;
                RebuildCaches();
                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Friends service unavailable: {exception.Message}");
                SocialErrorCode code = SocialErrorMapper.FromException(exception);
                return SocialResult.Failure(code, "Friends are unavailable right now.");
            }
        }

        public IReadOnlyList<FriendEntry> GetFriends()
        {
            return _friends;
        }

        public IReadOnlyList<FriendEntry> GetIncomingRequests()
        {
            return _incoming;
        }

        public IReadOnlyList<FriendEntry> GetOutgoingRequests()
        {
            return _outgoing;
        }

        public IReadOnlyList<FriendEntry> GetBlocked()
        {
            return _blocked;
        }

        public async Task<SocialResult> AddFriendByNameAsync(string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return SocialResult.Failure(SocialErrorCode.InvalidRequest, "Enter a player name, for example Player#1234.");
            }

            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.AddFriendByNameAsync(name.Trim());
                RebuildCaches();
            }, "That player could not be found.");
        }

        public async Task<SocialResult> AcceptFriendRequestAsync(string playerId, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.AddFriendAsync(playerId);
                RebuildCaches();
            }, "Could not accept that request.");
        }

        public async Task<SocialResult> DeclineFriendRequestAsync(string playerId, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.DeleteIncomingFriendRequestAsync(playerId);
                RebuildCaches();
            }, "Could not decline that request.");
        }

        public async Task<SocialResult> RemoveFriendAsync(string playerId, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.DeleteFriendAsync(playerId);
                RebuildCaches();
            }, "Could not remove that friend.");
        }

        public async Task<SocialResult> BlockPlayerAsync(string playerId, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.AddBlockAsync(playerId);
                RebuildCaches();
            }, "Could not block that player.");
        }

        public async Task<SocialResult> UnblockPlayerAsync(string playerId, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.DeleteBlockAsync(playerId);
                RebuildCaches();
            }, "Could not unblock that player.");
        }

        public async Task<SocialResult> SetOnlineAsync(string activity, CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.SetPresenceAsync(Availability.Online, new SocialActivity { Status = activity ?? string.Empty });
            }, "Could not publish presence.");
        }

        public async Task<SocialResult> NotifyClanInviteAsync(string targetPlayerId, string clanName, CancellationToken cancellationToken)
        {
            // Best effort only: a failed nudge must never fail the invite, which already exists
            // server-side and will show up on the target's next notification refresh.
            try
            {
                await FriendsService.Instance.MessageAsync(targetPlayerId, new SocialMessage
                {
                    Kind = _inviteMessagePrefix,
                    Body = clanName ?? string.Empty,
                });
                return SocialResult.Success();
            }
            catch (Exception)
            {
                return SocialResult.Success();
            }
        }

        public async Task<SocialResult> RefreshAsync(CancellationToken cancellationToken)
        {
            return await GuardAsync(async () =>
            {
                await FriendsService.Instance.ForceRelationshipsRefreshAsync();
                RebuildCaches();
            }, "Could not refresh friends.");
        }

        private async Task<SocialResult> GuardAsync(Func<Task> action, string failureMessage)
        {
            if (!_isReady)
            {
                return SocialResult.Failure(SocialErrorCode.ServiceUnavailable, "Friends are unavailable right now.");
            }

            try
            {
                await action();
                return SocialResult.Success();
            }
            catch (OperationCanceledException)
            {
                return SocialResult.Failure(SocialErrorCode.Cancelled, SocialErrorMapper.Describe(SocialErrorCode.Cancelled));
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"[ClanSystem] Friends call failed: {exception.Message}");
                SocialErrorCode code = SocialErrorMapper.FromException(exception);
                return SocialResult.Failure(code, failureMessage);
            }
        }

        private void RebuildCaches()
        {
            Fill(_friends, FriendsService.Instance.Friends, SocialRelationKind.Friend);
            Fill(_incoming, FriendsService.Instance.IncomingFriendRequests, SocialRelationKind.IncomingRequest);
            Fill(_outgoing, FriendsService.Instance.OutgoingFriendRequests, SocialRelationKind.OutgoingRequest);
            Fill(_blocked, FriendsService.Instance.Blocks, SocialRelationKind.Blocked);
            RelationshipsChanged?.Invoke();
        }

        private static void Fill(List<FriendEntry> target, IReadOnlyList<Relationship> source, SocialRelationKind kind)
        {
            target.Clear();
            if (source == null)
            {
                return;
            }

            for (int i = 0; i < source.Count; i++)
            {
                Relationship relationship = source[i];
                if (relationship == null || relationship.Member == null)
                {
                    continue;
                }

                target.Add(new FriendEntry
                {
                    PlayerId = relationship.Member.Id,
                    Name = relationship.Member.Profile != null ? relationship.Member.Profile.Name : null,
                    RelationshipId = relationship.Id,
                    Kind = kind,
                    Availability = MapAvailability(relationship.Member.Presence),
                    LastSeenUtc = relationship.Member.Presence != null ? relationship.Member.Presence.LastSeen : DateTime.MinValue,
                });
            }
        }

        private static SocialAvailability MapAvailability(Presence presence)
        {
            if (presence == null)
            {
                return SocialAvailability.Unknown;
            }

            switch (presence.Availability)
            {
                case Availability.Online: return SocialAvailability.Online;
                case Availability.Busy: return SocialAvailability.Busy;
                case Availability.Away: return SocialAvailability.Away;
                case Availability.Invisible: return SocialAvailability.Invisible;
                case Availability.Offline: return SocialAvailability.Offline;
                default: return SocialAvailability.Unknown;
            }
        }

        private void RelationshipAddedCallback(IRelationshipAddedEvent addedEvent)
        {
            RebuildCaches();
        }

        private void RelationshipDeletedCallback(IRelationshipDeletedEvent deletedEvent)
        {
            RebuildCaches();
        }

        private void PresenceUpdatedCallback(IPresenceUpdatedEvent presenceEvent)
        {
            RebuildCaches();
        }

        private void MessageReceivedCallback(IMessageReceivedEvent messageEvent)
        {
            try
            {
                SocialMessage message = messageEvent.GetAs<SocialMessage>();
                if (message != null && message.Kind == _inviteMessagePrefix)
                {
                    ClanInviteMessageReceived?.Invoke(messageEvent.UserId, message.Body);
                }
            }
            catch (Exception)
            {
                // A malformed message from another client is ignored rather than trusted.
            }
        }

        /// <summary>
        /// Presence activity payload published for friends to read.
        /// </summary>
        public class SocialActivity
        {
            public string Status { get; set; }

            public SocialActivity()
            {
                Status = string.Empty;
            }
        }

        /// <summary>
        /// Lightweight friend-to-friend message used only as an invite nudge.
        /// </summary>
        public class SocialMessage
        {
            public string Kind { get; set; }
            public string Body { get; set; }

            public SocialMessage()
            {
                Kind = string.Empty;
                Body = string.Empty;
            }
        }
    }
}
