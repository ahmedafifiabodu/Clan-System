using System;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Which communication channel a view is addressing. The clan channel name is derived from the
    /// server-known clan id; the client never chooses an arbitrary channel.
    /// </summary>
    public enum CommChannelKind
    {
        Global = 0,
        Clan = 1,
    }

    /// <summary>
    /// Lifecycle of the voice/text transport, surfaced so the UI can show an honest connection state
    /// instead of pretending everything is fine.
    /// </summary>
    public enum CommConnectionState
    {
        Disconnected = 0,
        Connecting = 1,
        Connected = 2,
        Recovering = 3,
        Failed = 4,
        NotConfigured = 5,
    }

    /// <summary>
    /// What a player is allowed to do in a channel.
    /// </summary>
    public enum CommMode
    {
        TextOnly = 0,
        AudioOnly = 1,
        TextAndAudio = 2,
    }

    /// <summary>
    /// A participant currently connected to a communication channel.
    /// </summary>
    [Serializable]
    public class CommParticipant
    {
        public string PlayerId { get; set; }
        public string DisplayName { get; set; }
        public string ChannelName { get; set; }
        public bool IsSelf { get; set; }
        public bool IsSpeaking { get; set; }
        public bool IsMuted { get; set; }
        public bool IsInAudio { get; set; }
        public int LocalVolume { get; set; }
    }

    /// <summary>
    /// A chat line as presented by the UI. Produced from the transport's own message type so the
    /// presentation layer never references the Vivox SDK directly.
    /// </summary>
    [Serializable]
    public class CommMessage
    {
        public string Id { get; set; }
        public string SenderId { get; set; }
        public string SenderName { get; set; }
        public string Text { get; set; }
        public DateTime TimestampUtc { get; set; }
        public CommChannelKind Channel { get; set; }
        public bool IsFromSelf { get; set; }

        public DateTime TimestampLocal => TimestampUtc.ToLocalTime();
    }
}
