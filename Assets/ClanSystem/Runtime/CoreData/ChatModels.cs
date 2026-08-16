using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Chat channels the client can address. Clan chat resolves server-side from the caller's
    /// membership, so no clan identifier is ever sent from the client.
    /// </summary>
    public enum ChatChannel
    {
        Global = 0,
        Clan = 1,
    }

    /// <summary>
    /// A single chat line.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ChatMessage
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("senderId")] public string SenderId { get; set; }
        [JsonProperty("senderName")] public string SenderName { get; set; }
        [JsonProperty("text")] public string Text { get; set; }
        [JsonProperty("ts")] public long TimestampMs { get; set; }
        [JsonProperty("channel")] public string ChannelRaw { get; set; }

        [JsonIgnore] public DateTime TimestampLocal => SocialTime.FromUnixMs(TimestampMs).ToLocalTime();
    }

    /// <summary>
    /// Incremental chat fetch response. <see cref="LatestTimestampMs"/> is fed back into the next
    /// poll so only new lines cross the wire.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ChatPage
    {
        [JsonProperty("messages")] public List<ChatMessage> Messages { get; set; }
        [JsonProperty("latestTs")] public long LatestTimestampMs { get; set; }
        [JsonProperty("channel")] public string ChannelRaw { get; set; }
    }

    /// <summary>
    /// Envelope returned when a message is accepted by the server.
    /// </summary>
    [Serializable]
    [JsonObject(MissingMemberHandling = MissingMemberHandling.Ignore)]
    public class ChatSendResponse
    {
        [JsonProperty("message")] public ChatMessage Message { get; set; }
    }
}
