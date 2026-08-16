using UnityEngine;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// Single source of tunables for the social stack: script names, leaderboard ids, limits and
    /// poll intervals. Nothing else in the client hard-codes a service identifier.
    /// Values that guard the backend (message length, clan size, cooldowns) are mirrored by the
    /// Cloud Code scripts, which remain the authority - these only drive local UI affordances.
    /// </summary>
    [CreateAssetMenu(fileName = "ClanSystemConfig", menuName = "Clan System/Config", order = 0)]
    public class ClanSystemConfig : ScriptableObject
    {
        [Header("Cloud Code scripts")]
        [SerializeField] private string _clanCommandScript = "SOCIAL_ClanCommand";
        [SerializeField] private string _clanQueryScript = "SOCIAL_ClanQuery";
        [SerializeField] private string _chatScript = "SOCIAL_Chat";
        [SerializeField] private string _leaderboardScript = "SOCIAL_Leaderboards";

        [Header("Leaderboard ids")]
        [SerializeField] private string _playerLeaderboardId = "player_score";
        [SerializeField] private string _clanLeaderboardId = "clan_score";

        [Header("Clan limits (server enforced)")]
        [SerializeField] private int _clanNameMinLength = 3;
        [SerializeField] private int _clanNameMaxLength = 24;
        [SerializeField] private int _clanTagMinLength = 2;
        [SerializeField] private int _clanTagMaxLength = 5;
        [SerializeField] private int _clanDescriptionMaxLength = 200;
        [SerializeField] private int _defaultMaxMembers = 30;

        [Header("Chat")]
        [SerializeField] private int _chatMessageMaxLength = 200;
        [SerializeField] private int _chatHistoryLimit = 50;
        [SerializeField] private float _chatPollIntervalSeconds = 3f;

        [Header("Vivox communication")]
        [SerializeField] private string _vivoxTokenScript = "SOCIAL_VivoxToken";
        [Tooltip("Name of the shared global channel. Must match the value allowed by SOCIAL_VivoxToken.js.")]
        [SerializeField] private string _globalChannelName = "global";
        [Tooltip("Clan channels are named <prefix><clanId>. The server rebuilds this name from the caller's real clan.")]
        [SerializeField] private string _clanChannelPrefix = "clan_";
        [SerializeField] private bool _autoJoinGlobalTextOnLogin = true;
        [SerializeField] private bool _joinVoiceMutedByDefault = true;
        [Tooltip("How often speaking indicators refresh, in seconds.")]
        [SerializeField] private float _voiceIndicatorRefreshSeconds = 0.2f;

        [Header("Emoji")]
        [SerializeField] private EmojiDatabase _emojiDatabase;

        [Header("Polling")]
        [SerializeField] private float _notificationPollIntervalSeconds = 15f;
        [SerializeField] private float _presenceHeartbeatSeconds = 60f;

        [Header("Paging")]
        [SerializeField] private int _leaderboardPageSize = 25;
        [SerializeField] private int _clanSearchPageSize = 20;

        [Header("Networking")]
        [SerializeField] private int _requestTimeoutSeconds = 20;
        [SerializeField] private int _requestRetryCount = 2;

        [Header("Demo")]
        [Tooltip("Score granted by the demo 'Play a match' button. The server clamps this value.")]
        [SerializeField] private int _demoScoreDelta = 120;

        public string ClanCommandScript => _clanCommandScript;
        public string ClanQueryScript => _clanQueryScript;
        public string ChatScript => _chatScript;
        public string LeaderboardScript => _leaderboardScript;

        public string PlayerLeaderboardId => _playerLeaderboardId;
        public string ClanLeaderboardId => _clanLeaderboardId;

        public int ClanNameMinLength => _clanNameMinLength;
        public int ClanNameMaxLength => _clanNameMaxLength;
        public int ClanTagMinLength => _clanTagMinLength;
        public int ClanTagMaxLength => _clanTagMaxLength;
        public int ClanDescriptionMaxLength => _clanDescriptionMaxLength;
        public int DefaultMaxMembers => _defaultMaxMembers;

        public int ChatMessageMaxLength => _chatMessageMaxLength;
        public int ChatHistoryLimit => _chatHistoryLimit;
        public float ChatPollIntervalSeconds => _chatPollIntervalSeconds;

        public string VivoxTokenScript => _vivoxTokenScript;
        public string GlobalChannelName => _globalChannelName;
        public string ClanChannelPrefix => _clanChannelPrefix;
        public bool IsAutoJoinGlobalTextOnLogin => _autoJoinGlobalTextOnLogin;
        public bool IsJoinVoiceMutedByDefault => _joinVoiceMutedByDefault;
        public float VoiceIndicatorRefreshSeconds => _voiceIndicatorRefreshSeconds;

        public EmojiDatabase EmojiDatabase => _emojiDatabase;

        /// <summary>
        /// Channel name for a clan. Mirrors the name the server authorises; the client still cannot
        /// join it without a token minted against its real membership.
        /// </summary>
        public string BuildClanChannelName(string clanId)
        {
            return string.IsNullOrEmpty(clanId) ? string.Empty : _clanChannelPrefix + clanId;
        }

        public float NotificationPollIntervalSeconds => _notificationPollIntervalSeconds;
        public float PresenceHeartbeatSeconds => _presenceHeartbeatSeconds;

        public int LeaderboardPageSize => _leaderboardPageSize;
        public int ClanSearchPageSize => _clanSearchPageSize;

        public int RequestTimeoutSeconds => _requestTimeoutSeconds;
        public int RequestRetryCount => _requestRetryCount;

        public int DemoScoreDelta => _demoScoreDelta;
    }
}
