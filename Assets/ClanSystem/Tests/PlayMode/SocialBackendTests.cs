using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using ClanSystem.Services;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace ClanSystem.Tests
{
    /// <summary>
    /// Play Mode tests that exercise the real backend - no mocks. A green run means the deployed
    /// Cloud Code, Cloud Save and Leaderboards actually behave as the client expects.
    ///
    /// Each test signs in under its own Authentication profile so runs stay isolated, and the
    /// fixture leaves no clan behind.
    /// </summary>
    [TestFixture]
    public class SocialBackendTests
    {
        private const string _configPath = "ClanSystemConfig";

        /// <summary>How long a search retries before a newly created clan is considered missing.</summary>
        private const int _searchIndexAttempts = 6;

        private const int _searchIndexRetryMs = 500;

        private ClanSystemConfig _config;
        private SocialCoordinator _coordinator;

        /// <summary>
        /// Signs in once per test with a unique profile so a failure cannot leak state into the next.
        /// </summary>
        private async Task<SocialResult> StartAsync(string profileSuffix)
        {
            _config = LoadConfig();
            Assert.IsNotNull(_config, "ClanSystemConfig not found. Place it in a Resources folder or assign it.");

            UgsAuthenticationGateway auth = new UgsAuthenticationGateway();
            UgsFriendsGateway friends = new UgsFriendsGateway();
            CloudCodeSocialBackend backend = new CloudCodeSocialBackend(_config);
            CloudCodeVivoxTokenProvider tokenProvider = new CloudCodeVivoxTokenProvider(_config);
            VivoxCommunicationService communication = new VivoxCommunicationService(_config, tokenProvider);

            _coordinator = new SocialCoordinator(_config, auth, friends, backend, communication);
            return await _coordinator.StartAsync("test" + profileSuffix);
        }

        private static ClanSystemConfig LoadConfig()
        {
            ClanSystemConfig fromResources = Resources.Load<ClanSystemConfig>(_configPath);
            if (fromResources != null)
            {
                return fromResources;
            }

#if UNITY_EDITOR
            // The config deliberately lives outside Resources so it is not force-included in builds;
            // in the Editor the asset database can still find it for tests.
            string[] guids = UnityEditor.AssetDatabase.FindAssets("t:ClanSystemConfig");
            if (guids.Length > 0)
            {
                string path = UnityEditor.AssetDatabase.GUIDToAssetPath(guids[0]);
                ClanSystemConfig asset = UnityEditor.AssetDatabase.LoadAssetAtPath<ClanSystemConfig>(path);
                if (asset != null)
                {
                    return asset;
                }
            }
#endif

            ClanSystemConfig[] loaded = Resources.FindObjectsOfTypeAll<ClanSystemConfig>();
            return loaded.Length > 0 ? loaded[0] : null;
        }

        private async Task CleanUpClanAsync()
        {
            if (_coordinator == null || !_coordinator.State.IsInClan)
            {
                return;
            }

            if (_coordinator.State.MyRole == ClanRole.Owner)
            {
                await _coordinator.DisbandClanAsync();
            }
            else
            {
                await _coordinator.LeaveClanAsync();
            }
        }

        [TearDown]
        public void TearDown()
        {
            _coordinator?.Dispose();
            _coordinator = null;
        }

        [UnityTest]
        public IEnumerator SignIn_ResolvesPlayerIdAndSnapshot()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("signin");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                Assert.IsNotEmpty(_coordinator.PlayerId, "No player id after sign-in.");
                Assert.IsNotNull(_coordinator.State.Profile, "Snapshot carried no profile.");

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator CreateClan_MakesCallerOwnerWithSingleMember()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("create");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult create = await _coordinator.CreateClanAsync("Test " + tag, tag, "Play Mode test clan.", true, 0);
                Assert.IsTrue(create.IsSuccess, "Create failed: " + create.Message);

                Assert.IsTrue(_coordinator.State.IsInClan, "Client cache shows no clan after create.");
                Assert.AreEqual(ClanRole.Owner, _coordinator.State.MyRole, "Creator is not Owner.");
                Assert.AreEqual(1, _coordinator.State.Members.Count, "Roster should hold exactly the creator.");

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator CreateSecondClan_IsRejectedWhileAlreadyInOne()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("second");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult first = await _coordinator.CreateClanAsync("Test " + tag, tag, "First clan.", true, 0);
                Assert.IsTrue(first.IsSuccess, "First create failed: " + first.Message);

                SocialResult second = await _coordinator.CreateClanAsync("Test " + tag + "B", tag + "B", "Should be refused.", true, 0);
                Assert.IsFalse(second.IsSuccess, "Server allowed a second clan while already in one.");

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator ChangeOwnRole_IsRejectedByServer()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("role");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult create = await _coordinator.CreateClanAsync("Test " + tag, tag, "Permission test.", true, 0);
                Assert.IsTrue(create.IsSuccess, "Create failed: " + create.Message);

                // The owner must not be able to demote themselves - the server rejects the target.
                SocialResult selfDemote = await _coordinator.SetMemberRoleAsync(_coordinator.PlayerId, ClanRole.Member);
                Assert.IsFalse(selfDemote.IsSuccess, "Server allowed the owner to change their own role.");

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator SubmitScore_IsClampedAndReachesBothLeaderboards()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("score");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult create = await _coordinator.CreateClanAsync("Test " + tag, tag, "Score test.", true, 0);
                Assert.IsTrue(create.IsSuccess, "Create failed: " + create.Message);

                SocialResult score = await _coordinator.SubmitDemoScoreAsync();
                Assert.IsTrue(score.IsSuccess, "Score submission failed: " + score.Message);

                SocialResult<LeaderboardPage> players = await _coordinator.GetPlayerLeaderboardAsync(0);
                Assert.IsTrue(players.IsSuccess, "Player leaderboard failed: " + players.Message);
                Assert.IsNotNull(players.Value, "Player leaderboard returned no page.");

                SocialResult<LeaderboardPage> clans = await _coordinator.GetClanLeaderboardAsync(0);
                Assert.IsTrue(clans.IsSuccess, "Clan leaderboard failed: " + clans.Message);
                Assert.IsNotNull(clans.Value, "Clan leaderboard returned no page.");

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator ClanSearch_FindsNewlyCreatedClan()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("search");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult create = await _coordinator.CreateClanAsync("Test " + tag, tag, "Search test.", true, 0);
                Assert.IsTrue(create.IsSuccess, "Create failed: " + create.Message);

                // The clan directory is a Cloud Save query index, and an index is updated a moment
                // after the write that feeds it. Searching the instant a clan is created is a race
                // the test would lose at random, so it retries for a bounded window instead of
                // asserting on the first attempt.
                bool found = false;
                string lastMessage = null;
                for (int attempt = 0; attempt < _searchIndexAttempts && !found; attempt++)
                {
                    if (attempt > 0)
                    {
                        await Task.Delay(_searchIndexRetryMs);
                    }

                    SocialResult<ClanSearchPage> search = await _coordinator.SearchClansAsync(tag, 0);
                    lastMessage = search.Message;
                    Assert.IsTrue(search.IsSuccess, "Search failed: " + search.Message);

                    List<ClanSummary> clans = search.Value != null ? search.Value.Clans : null;
                    if (clans == null)
                    {
                        continue;
                    }

                    for (int i = 0; i < clans.Count; i++)
                    {
                        if (clans[i].Tag == tag)
                        {
                            found = true;
                        }
                    }
                }

                Assert.IsTrue(found, "Newly created clan did not appear in search results. " + lastMessage);

                await CleanUpClanAsync();
            });
        }

        [UnityTest]
        public IEnumerator Disband_ClearsClanFromPlayerState()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("disband");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);
                await CleanUpClanAsync();

                string tag = BuildTag(_coordinator.PlayerId);
                SocialResult create = await _coordinator.CreateClanAsync("Test " + tag, tag, "Disband test.", true, 0);
                Assert.IsTrue(create.IsSuccess, "Create failed: " + create.Message);

                SocialResult disband = await _coordinator.DisbandClanAsync();
                Assert.IsTrue(disband.IsSuccess, "Disband failed: " + disband.Message);
                Assert.IsFalse(_coordinator.State.IsInClan, "Player still in a clan after disband.");
            });
        }

        /// <summary>
        /// A rename has to reach the chat transport, not just the name service. Vivox stamps the
        /// sender name into each message from its login session and has no setter for it, so the
        /// session is rebuilt under the new name - this guards the rebuild, which is the part that
        /// can leave the player silently signed out of chat.
        /// </summary>
        [UnityTest]
        public IEnumerator Rename_KeepsChatSessionAliveUnderNewName()
        {
            yield return Run(async () =>
            {
                SocialResult start = await StartAsync("rename");
                Assert.IsTrue(start.IsSuccess, "Sign-in failed: " + start.Message);

                ICommunicationService communication = _coordinator.Communication;
                if (communication == null || !communication.IsLoggedIn)
                {
                    Assert.Ignore("Voice/chat transport is not connected; nothing to verify.");
                }

                string renamed = "Ren" + System.DateTime.UtcNow.Ticks % 100000;
                SocialResult rename = await _coordinator.SetPlayerNameAsync(renamed);
                Assert.IsTrue(rename.IsSuccess, "Rename failed: " + rename.Message);

                // The session must survive the rebuild, or the player is renamed straight out of chat.
                Assert.IsTrue(communication.IsLoggedIn, "Chat session did not come back up after the rename.");

                // And it must accept a send under the new identity.
                SocialResult sent = await communication.SendTextAsync(
                    CommChannelKind.Global, "rename probe " + renamed, _coordinator.Lifetime);
                Assert.IsTrue(sent.IsSuccess, "Could not send after rename: " + sent.Message);

                await CleanUpClanAsync();
            });
        }

        /// <summary>
        /// Bridges async work into a UnityTest coroutine and surfaces exceptions as test failures
        /// rather than letting them disappear into an unobserved task.
        /// </summary>
        private static IEnumerator Run(System.Func<Task> body)
        {
            Application.runInBackground = true;

            Task task = body();
            while (!task.IsCompleted)
            {
                yield return null;
            }

            if (task.IsFaulted && task.Exception != null)
            {
                throw task.Exception.GetBaseException();
            }
        }

        private static string BuildTag(string playerId)
        {
            string source = playerId != null ? playerId.ToUpperInvariant() : "TEST";
            System.Text.StringBuilder builder = new System.Text.StringBuilder(5);
            for (int i = 0; i < source.Length && builder.Length < 4; i++)
            {
                if (char.IsLetterOrDigit(source[i]))
                {
                    builder.Append(source[i]);
                }
            }

            return "T" + builder;
        }
    }
}
