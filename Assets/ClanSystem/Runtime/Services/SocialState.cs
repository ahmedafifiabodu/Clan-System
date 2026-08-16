using System;
using System.Collections.Generic;
using ClanSystem.CoreData;

namespace ClanSystem.Services
{
    /// <summary>
    /// The client's cached view of everything the server owns. Views read from here and subscribe
    /// to the change events rather than re-querying the backend on every repaint.
    /// The cache is authoritative only for rendering: every mutation is still a server round trip.
    /// </summary>
    public class SocialState
    {
        private readonly List<ClanMember> _members = new List<ClanMember>();
        private readonly List<ClanInvite> _invites = new List<ClanInvite>();
        private readonly List<ClanJoinRequest> _joinRequests = new List<ClanJoinRequest>();
        private readonly List<FriendEntry> _friends = new List<FriendEntry>();

        public PlayerSocialProfile Profile { get; private set; }
        public ClanProfile Clan { get; private set; }

        public IReadOnlyList<ClanMember> Members => _members;
        public IReadOnlyList<ClanInvite> Invites => _invites;
        public IReadOnlyList<ClanJoinRequest> JoinRequests => _joinRequests;
        public IReadOnlyList<FriendEntry> Friends => _friends;


        public bool IsInClan => Clan != null;
        public ClanRole MyRole => Profile != null ? Profile.Role : ClanRole.None;

        public event Action ClanChanged;

        /// <summary>
        /// Raised with the new clan id (null when clanless) only when membership actually changes.
        /// </summary>
        public event Action<string> ClanMembershipChanged;

        public event Action MembersChanged;
        public event Action NotificationsChanged;
        public event Action FriendsChanged;

        public int PendingNotificationCount => _invites.Count + _joinRequests.Count;

        public void ApplySnapshot(SocialSnapshot snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            string previousClanId = Clan != null ? Clan.ClanId : null;
            Profile = snapshot.Profile;
            Clan = snapshot.Clan;

            _members.Clear();
            if (snapshot.Members != null)
            {
                _members.AddRange(snapshot.Members);
            }

            _invites.Clear();
            if (snapshot.Invites != null)
            {
                _invites.AddRange(snapshot.Invites);
            }

            _joinRequests.Clear();
            if (snapshot.JoinRequests != null)
            {
                _joinRequests.AddRange(snapshot.JoinRequests);
            }

            string currentClanId = Clan != null ? Clan.ClanId : null;
            if (previousClanId != currentClanId)
            {
                // Joined, left, kicked or switched: the communication layer listens for this and
                // moves the player off the old clan channel and onto the new one.
                ClanMembershipChanged?.Invoke(currentClanId);
            }

            ClanChanged?.Invoke();
            MembersChanged?.Invoke();
            NotificationsChanged?.Invoke();
        }

        public void SetMembers(List<ClanMember> members)
        {
            _members.Clear();
            if (members != null)
            {
                _members.AddRange(members);
            }

            MembersChanged?.Invoke();
        }

        public void SetFriends(List<FriendEntry> friends)
        {
            _friends.Clear();
            if (friends != null)
            {
                _friends.AddRange(friends);
            }

            FriendsChanged?.Invoke();
        }

        public void Reset()
        {
            Profile = null;
            Clan = null;
            _members.Clear();
            _invites.Clear();
            _joinRequests.Clear();
            _friends.Clear();
        }

    }
}
