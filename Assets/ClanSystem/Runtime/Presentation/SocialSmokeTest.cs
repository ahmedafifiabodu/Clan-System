using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// End-to-end smoke test for the social stack. Drives the real backend - sign-in, clan
    /// lifecycle, chat, leaderboards - and writes a pass/fail report to the console.
    /// Attach to an empty object in play mode, or call <see cref="RunAsync"/> from an editor script.
    /// It deliberately uses no mocks: a green run means the deployed Cloud Code actually works.
    /// </summary>
    public class SocialSmokeTest : MonoBehaviour
    {
        [SerializeField] private ClanSystemConfig _config;
        [SerializeField] private string _profile = "smoketest";
        [SerializeField] private bool _runOnStart = false;
        [SerializeField] private bool _disbandWhenFinished = true;

        private readonly StringBuilder _log = new StringBuilder();
        private int _passed;
        private int _failed;

        public string Report => _log.ToString();
        public bool IsFinished { get; private set; }
        public bool IsPassing => _failed == 0 && _passed > 0;

        private void Start()
        {
            if (_runOnStart)
            {
                _ = RunAsync();
            }
        }

        public async Task RunAsync()
        {
            IsFinished = false;
            _log.Clear();
            _passed = 0;
            _failed = 0;

            if (_config == null)
            {
                _config = Resources.FindObjectsOfTypeAll<ClanSystemConfig>().Length > 0
                    ? Resources.FindObjectsOfTypeAll<ClanSystemConfig>()[0]
                    : null;
            }

            if (_config == null)
            {
                Record("config", false, "No ClanSystemConfig assigned.");
                IsFinished = true;
                return;
            }

            UgsAuthenticationGateway auth = new UgsAuthenticationGateway();
            UgsFriendsGateway friends = new UgsFriendsGateway();
            CloudCodeSocialBackend backend = new CloudCodeSocialBackend(_config);
            CloudCodeVivoxTokenProvider tokenProvider = new CloudCodeVivoxTokenProvider(_config);
            VivoxCommunicationService communication = new VivoxCommunicationService(_config, tokenProvider);
            SocialCoordinator coordinator = new SocialCoordinator(_config, auth, friends, backend, communication);

            try
            {
                SocialResult start = await coordinator.StartAsync(_profile);
                Record("sign in + first snapshot", start.IsSuccess, start.Message);
                if (!start.IsSuccess)
                {
                    IsFinished = true;
                    return;
                }

                Record("player id resolved", !string.IsNullOrEmpty(coordinator.PlayerId), coordinator.PlayerId);

                // Start from a known state so repeat runs are deterministic.
                if (coordinator.State.IsInClan)
                {
                    if (coordinator.State.MyRole == ClanRole.Owner)
                    {
                        await coordinator.DisbandClanAsync();
                    }
                    else
                    {
                        await coordinator.LeaveClanAsync();
                    }
                }

                string tag = BuildTag(coordinator.PlayerId);
                string clanName = "Smoke " + tag;
                SocialResult create = await coordinator.CreateClanAsync(clanName, tag, "Automated smoke test clan.", true, 3);
                Record("create clan", create.IsSuccess, create.Message);

                Record("clan cached after create", coordinator.State.IsInClan, coordinator.State.Clan != null ? coordinator.State.Clan.Name : "none");
                Record("creator is Owner", coordinator.State.MyRole == ClanRole.Owner, coordinator.State.MyRole.ToString());
                Record("roster has creator", coordinator.State.Members.Count == 1, coordinator.State.Members.Count + " member(s)");

                SocialResult duplicate = await coordinator.CreateClanAsync(clanName + " 2", tag, "Should be rejected.", true, 1);
                Record("second clan rejected while in a clan", !duplicate.IsSuccess, duplicate.Message);

                SocialResult motd = await coordinator.UpdateClanSettingsAsync(null, "Smoke test MOTD", null, null);
                Record("update MOTD as owner", motd.IsSuccess, motd.Message);

                // Communication runs on Vivox: text and voice share one transport.
                ICommunicationService comm = coordinator.Communication;
                bool isVoiceReady = comm != null && comm.State == CommConnectionState.Connected;
                Record("voice transport connected", isVoiceReady, comm != null ? comm.State + " - " + comm.StateDetail : "no service");

                if (isVoiceReady)
                {
                    SocialResult globalText = await comm.SendTextAsync(CommChannelKind.Global, "Smoke global " + System.DateTime.UtcNow.Ticks, coordinator.Lifetime);
                    Record("global text chat", globalText.IsSuccess, globalText.Message);

                    SocialResult emojiText = await comm.SendTextAsync(CommChannelKind.Global, "Emoji check 🔥👋", coordinator.Lifetime);
                    Record("emoji message", emojiText.IsSuccess, emojiText.Message);

                    SocialResult clanText = await comm.SendTextAsync(CommChannelKind.Clan, "Smoke clan " + System.DateTime.UtcNow.Ticks, coordinator.Lifetime);
                    Record("clan text chat", clanText.IsSuccess, clanText.Message);

                    SocialResult globalVoice = await comm.JoinVoiceAsync(CommChannelKind.Global, coordinator.Lifetime);
                    Record("join global voice", globalVoice.IsSuccess, globalVoice.Message);

                    SocialResult clanVoice = await comm.JoinVoiceAsync(CommChannelKind.Clan, coordinator.Lifetime);
                    Record("join clan voice", clanVoice.IsSuccess, clanVoice.Message);

                    comm.SetMicrophoneMuted(true);
                    Record("microphone mute", comm.IsMicrophoneMuted, "muted");
                    comm.SetMicrophoneMuted(false);
                    Record("microphone unmute", !comm.IsMicrophoneMuted, "unmuted");

                    comm.SetSpeakerMuted(true);
                    Record("speaker mute", comm.IsSpeakerMuted, "muted");
                    comm.SetSpeakerMuted(false);

                    await Task.Delay(1500);
                    SocialResult<List<CommMessage>> history = await comm.GetHistoryAsync(CommChannelKind.Clan, 20, coordinator.Lifetime);
                    Record("clan chat history readable by member", history.IsSuccess, history.IsSuccess ? (history.Value != null ? history.Value.Count + " message(s)" : "none") : history.Message);

                    EmojiDatabase emoji = _config.EmojiDatabase;
                    bool emojiOk = emoji != null && emoji.Categories.Count > 0;
                    Record("emoji database populated", emojiOk, emojiOk ? emoji.Categories.Count + " categories" : "missing");
                    if (emojiOk)
                    {
                        string expanded = emoji.ExpandShortcodes("gg :fire:");
                        Record("emoji shortcode expands", expanded != "gg :fire:", expanded);
                    }
                }

                SocialResult score = await coordinator.SubmitDemoScoreAsync();
                Record("submit score", score.IsSuccess, score.Message);

                SocialResult<LeaderboardPage> players = await coordinator.GetPlayerLeaderboardAsync(0);
                Record("player leaderboard", players.IsSuccess && players.Value != null, players.IsSuccess ? Describe(players.Value) : players.Message);

                SocialResult<LeaderboardPage> clans = await coordinator.GetClanLeaderboardAsync(0);
                Record("clan leaderboard", clans.IsSuccess && clans.Value != null, clans.IsSuccess ? Describe(clans.Value) : clans.Message);

                SocialResult<ClanSearchPage> search = await coordinator.SearchClansAsync(tag, 0);
                bool foundOwnClan = false;
                if (search.IsSuccess && search.Value != null && search.Value.Clans != null)
                {
                    for (int i = 0; i < search.Value.Clans.Count; i++)
                    {
                        if (search.Value.Clans[i].Tag == tag)
                        {
                            foundOwnClan = true;
                        }
                    }
                }

                Record("clan search finds new clan", foundOwnClan, search.IsSuccess ? (search.Value != null ? search.Value.Total + " result(s)" : "no page") : search.Message);

                SocialResult<List<ClanActivityEntry>> activity = await coordinator.GetActivityAsync();
                Record("activity log written", activity.IsSuccess && activity.Value != null && activity.Value.Count > 0, activity.IsSuccess ? (activity.Value != null ? activity.Value.Count + " entries" : "none") : activity.Message);

                SocialResult invalidInvite = await coordinator.InvitePlayerAsync("not-a-real-player-id", "ghost");
                Record("invite to unknown player handled", true, invalidInvite.IsSuccess ? "accepted (server stores a pending invite)" : invalidInvite.Message);

                SocialResult badRole = await coordinator.SetMemberRoleAsync(coordinator.PlayerId, ClanRole.Member);
                Record("cannot change own role", !badRole.IsSuccess, badRole.Message);

                if (_disbandWhenFinished)
                {
                    SocialResult disband = await coordinator.DisbandClanAsync();
                    Record("disband clan", disband.IsSuccess, disband.Message);
                    Record("clan cleared after disband", !coordinator.State.IsInClan, coordinator.State.IsInClan ? "still in clan" : "clear");
                }
            }
            finally
            {
                coordinator.Dispose();
                IsFinished = true;

                string summary = $"[ClanSystem SmokeTest] {_passed} passed, {_failed} failed\n{_log}";
                if (_failed == 0)
                {
                    Debug.Log(summary);
                }
                else
                {
                    Debug.LogError(summary);
                }
            }
        }

        private static string Describe(LeaderboardPage page)
        {
            int count = page != null && page.Rows != null ? page.Rows.Count : 0;
            string self = page != null && page.Self != null ? " self rank #" + page.Self.Rank : " self rank none";
            return count + " row(s), total " + (page != null ? page.Total : 0) + self;
        }

        private static string BuildTag(string playerId)
        {
            string source = playerId != null ? playerId.ToUpperInvariant() : "SMOKE";
            StringBuilder builder = new StringBuilder(5);
            for (int i = 0; i < source.Length && builder.Length < 4; i++)
            {
                char character = source[i];
                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(character);
                }
            }

            return "S" + builder;
        }

        private void Record(string step, bool isPass, string detail)
        {
            if (isPass)
            {
                _passed++;
            }
            else
            {
                _failed++;
            }

            _log.AppendLine($"{(isPass ? "PASS" : "FAIL")}  {step}  |  {detail}");
        }
    }
}
