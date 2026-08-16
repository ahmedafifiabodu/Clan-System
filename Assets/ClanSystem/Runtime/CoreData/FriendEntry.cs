using System;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Availability of another player, mirroring the Friends service presence values so the
    /// presentation layer never has to reference the Friends SDK directly.
    /// </summary>
    public enum SocialAvailability
    {
        Unknown = 0,
        Online = 1,
        Busy = 2,
        Away = 3,
        Invisible = 4,
        Offline = 5,
    }

    /// <summary>
    /// Kind of relationship the local player has with another player.
    /// </summary>
    public enum SocialRelationKind
    {
        Friend = 0,
        IncomingRequest = 1,
        OutgoingRequest = 2,
        Blocked = 3,
    }

    /// <summary>
    /// A friend list row: Friends service identity merged with the clan info owned by Cloud Code.
    /// </summary>
    [Serializable]
    public class FriendEntry
    {
        public string PlayerId { get; set; }
        public string Name { get; set; }
        public string RelationshipId { get; set; }
        public SocialRelationKind Kind { get; set; }
        public SocialAvailability Availability { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public string ClanId { get; set; }
        public string ClanName { get; set; }
        public string ClanTag { get; set; }
        public long Score { get; set; }

        public bool IsOnline => Availability == SocialAvailability.Online
            || Availability == SocialAvailability.Busy
            || Availability == SocialAvailability.Away;

        public bool IsInClan => !string.IsNullOrEmpty(ClanId);
    }
}
