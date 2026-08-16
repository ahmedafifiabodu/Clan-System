using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// One row of either leaderboard. Clan rows fill <see cref="Tag"/> and <see cref="MemberCount"/>;
    /// player rows fill <see cref="ClanTag"/> instead.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class LeaderboardRow
    {
        [JsonProperty("rank")] public int Rank { get; set; }
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("score")] public double Score { get; set; }
        [JsonProperty("tag")] public string Tag { get; set; }
        [JsonProperty("clanTag")] public string ClanTag { get; set; }
        [JsonProperty("memberCount")] public int MemberCount { get; set; }
        [JsonProperty("maxMembers")] public int MaxMembers { get; set; }
        [JsonProperty("level")] public int Level { get; set; }
        [JsonProperty("isSelf")] public bool IsSelf { get; set; }
    }

    /// <summary>
    /// Paged leaderboard response. <see cref="Self"/> is the caller's own row even when it falls
    /// outside the requested page.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class LeaderboardPage
    {
        [JsonProperty("rows")] public List<LeaderboardRow> Rows { get; set; }
        [JsonProperty("total")] public int Total { get; set; }
        [JsonProperty("offset")] public int Offset { get; set; }
        [JsonProperty("limit")] public int Limit { get; set; }
        [JsonProperty("self")] public LeaderboardRow Self { get; set; }
        [JsonProperty("myClanId")] public string MyClanId { get; set; }
    }

    /// <summary>
    /// Server response to a score submission: the authoritative totals after clamping.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ScoreSubmitResponse
    {
        [JsonProperty("playerScore")] public long PlayerScore { get; set; }
        [JsonProperty("delta")] public long AcceptedDelta { get; set; }
        [JsonProperty("clanScore")] public long ClanScore { get; set; }
        [JsonProperty("clanLevel")] public int ClanLevel { get; set; }
    }
}
