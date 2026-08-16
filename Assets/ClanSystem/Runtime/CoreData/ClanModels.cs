using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Public view of a clan as returned by Cloud Code. Field names mirror the server payload
    /// exactly so the DTO and the wire format cannot drift apart silently.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanProfile
    {
        [JsonProperty("clanId")] public string ClanId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("motd")] public string Motd { get; set; }
        [JsonProperty("ownerId")] public string OwnerId { get; set; }
        [JsonProperty("ownerName")] public string OwnerName { get; set; }
        [JsonProperty("createdAt")] public long CreatedAtMs { get; set; }
        [JsonProperty("memberCount")] public int MemberCount { get; set; }
        [JsonProperty("maxMembers")] public int MaxMembers { get; set; }
        [JsonProperty("isPublic")] public bool IsPublic { get; set; }
        [JsonProperty("emblemId")] public int EmblemId { get; set; }
        [JsonProperty("score")] public long Score { get; set; }
        [JsonProperty("xp")] public long Xp { get; set; }
        [JsonProperty("level")] public int Level { get; set; }

        [JsonIgnore] public DateTime CreatedAtUtc => SocialTime.FromUnixMs(CreatedAtMs);
    }

    /// <summary>
    /// A single roster entry of the caller's own clan.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanMember
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("role")] public string RoleRaw { get; set; }
        [JsonProperty("joinedAt")] public long JoinedAtMs { get; set; }
        [JsonProperty("contribution")] public long Contribution { get; set; }
        [JsonProperty("lastActive")] public long LastActiveMs { get; set; }

        [JsonIgnore] public ClanRole Role => SocialEnum.ParseRole(RoleRaw);
        [JsonIgnore] public DateTime LastActiveUtc => SocialTime.FromUnixMs(LastActiveMs);
    }

    /// <summary>
    /// Compact clan record used by search results and directory listings.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanSummary
    {
        [JsonProperty("clanId")] public string ClanId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("memberCount")] public int MemberCount { get; set; }
        [JsonProperty("maxMembers")] public int MaxMembers { get; set; }
        [JsonProperty("score")] public long Score { get; set; }
        [JsonProperty("level")] public int Level { get; set; }
        [JsonProperty("emblemId")] public int EmblemId { get; set; }
        [JsonProperty("isPublic")] public bool IsPublic { get; set; }
    }

    /// <summary>
    /// Pending clan invitation addressed to the local player.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanInvite
    {
        [JsonProperty("inviteId")] public string InviteId { get; set; }
        [JsonProperty("clanId")] public string ClanId { get; set; }
        [JsonProperty("clanName")] public string ClanName { get; set; }
        [JsonProperty("clanTag")] public string ClanTag { get; set; }
        [JsonProperty("senderId")] public string SenderId { get; set; }
        [JsonProperty("senderName")] public string SenderName { get; set; }
        [JsonProperty("receiverId")] public string ReceiverId { get; set; }
        [JsonProperty("createdAt")] public long CreatedAtMs { get; set; }
        [JsonProperty("expiresAt")] public long ExpiresAtMs { get; set; }

        [JsonIgnore] public DateTime ExpiresAtUtc => SocialTime.FromUnixMs(ExpiresAtMs);
    }

    /// <summary>
    /// Pending request from a player asking to join the caller's clan.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanJoinRequest
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("message")] public string Message { get; set; }
        [JsonProperty("createdAt")] public long CreatedAtMs { get; set; }
    }

    /// <summary>
    /// One line of the clan activity feed.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanActivityEntry
    {
        [JsonProperty("ts")] public long TimestampMs { get; set; }
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("actorName")] public string ActorName { get; set; }
        [JsonProperty("text")] public string Text { get; set; }

        [JsonIgnore] public DateTime TimestampUtc => SocialTime.FromUnixMs(TimestampMs);
    }

    /// <summary>
    /// The local player's social record: identity plus clan membership and score.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class PlayerSocialProfile
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("clanId")] public string ClanId { get; set; }
        [JsonProperty("role")] public string RoleRaw { get; set; }
        [JsonProperty("score")] public long Score { get; set; }
        [JsonProperty("contribution")] public long Contribution { get; set; }
        [JsonProperty("joinedAt")] public long JoinedAtMs { get; set; }

        [JsonIgnore] public ClanRole Role => SocialEnum.ParseRole(RoleRaw);
        [JsonIgnore] public bool IsInClan => !string.IsNullOrEmpty(ClanId);
    }

    /// <summary>
    /// Social info about another player, used by the friends list and the profile popup.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class PlayerSocialInfo
    {
        [JsonProperty("playerId")] public string PlayerId { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("clanId")] public string ClanId { get; set; }
        [JsonProperty("clanName")] public string ClanName { get; set; }
        [JsonProperty("clanTag")] public string ClanTag { get; set; }
        [JsonProperty("role")] public string RoleRaw { get; set; }
        [JsonProperty("score")] public long Score { get; set; }
        [JsonProperty("lastActive")] public long LastActiveMs { get; set; }

        [JsonIgnore] public ClanRole Role => SocialEnum.ParseRole(RoleRaw);
    }

    /// <summary>
    /// Snapshot returned by the "me" query: everything the social window needs on open.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class SocialSnapshot
    {
        [JsonProperty("profile")] public PlayerSocialProfile Profile { get; set; }
        [JsonProperty("clan")] public ClanProfile Clan { get; set; }
        [JsonProperty("members")] public List<ClanMember> Members { get; set; }
        [JsonProperty("invites")] public List<ClanInvite> Invites { get; set; }
        [JsonProperty("joinRequests")] public List<ClanJoinRequest> JoinRequests { get; set; }
    }

    /// <summary>
    /// Paged clan search response.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanSearchPage
    {
        [JsonProperty("clans")] public List<ClanSummary> Clans { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
    }

    /// <summary>
    /// Detail view of a clan the caller may or may not belong to. <see cref="Members"/> is only
    /// populated by the server when the caller is a member.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ClanDetail
    {
        [JsonProperty("clan")] public ClanProfile Clan { get; set; }
        [JsonProperty("members")] public List<ClanMember> Members { get; set; }
        [JsonProperty("isMember")] public bool IsMember { get; set; }
    }
}
