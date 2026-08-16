using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Clan tab. Shows either the create/search view or the clan dashboard, depending on whether
    /// the player is in a clan. Leader-only controls are hidden for lower ranks purely as an
    /// affordance - Cloud Code rejects the same calls regardless of what the client displays.
    /// </summary>
    public class ClanTabController
    {
        private static readonly Color[] _emblemColors =
        {
            new Color(0.19f, 0.43f, 0.78f),
            new Color(0.74f, 0.29f, 0.28f),
            new Color(0.25f, 0.62f, 0.42f),
            new Color(0.61f, 0.44f, 0.16f),
            new Color(0.47f, 0.32f, 0.68f),
            new Color(0.20f, 0.58f, 0.65f),
            new Color(0.78f, 0.45f, 0.19f),
            new Color(0.42f, 0.49f, 0.24f),
            new Color(0.66f, 0.26f, 0.50f),
            new Color(0.29f, 0.35f, 0.62f),
            new Color(0.55f, 0.55f, 0.58f),
            new Color(0.24f, 0.51f, 0.30f),
        };

        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly PlayerProfilePopup _popup;

        private readonly VisualElement _noneView;
        private readonly VisualElement _detailView;
        private readonly TextField _createName;
        private readonly TextField _createTag;
        private readonly TextField _createDescription;
        private readonly Toggle _createPublic;
        private readonly TextField _searchField;
        private readonly ScrollView _searchList;
        private readonly Label _searchEmpty;

        private readonly VisualElement _emblem;
        private readonly Label _clanName;
        private readonly Label _clanSub;
        private readonly Label _clanDescription;
        private readonly Label _clanScore;
        private readonly Label _clanRank;
        private readonly Label _clanMembers;
        private readonly TextField _motdField;
        private readonly Button _motdButton;
        private readonly ScrollView _membersList;
        private readonly ScrollView _activityList;
        private readonly Button _leaveButton;
        private readonly Button _disbandButton;

        private int _cachedClanRank;

        public ClanTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window, PlayerProfilePopup popup)
        {
            _coordinator = coordinator;
            _window = window;
            _popup = popup;

            _noneView = root.Q<VisualElement>("clan-none");
            _detailView = root.Q<VisualElement>("clan-detail");

            _createName = root.Q<TextField>("create-name-field");
            _createTag = root.Q<TextField>("create-tag-field");
            _createDescription = root.Q<TextField>("create-description-field");
            _createPublic = root.Q<Toggle>("create-public-toggle");
            _searchField = root.Q<TextField>("search-field");
            _searchList = root.Q<ScrollView>("search-list");
            _searchEmpty = root.Q<Label>("search-empty");

            _emblem = root.Q<VisualElement>("clan-emblem");
            _clanName = root.Q<Label>("clan-name");
            _clanSub = root.Q<Label>("clan-sub");
            _clanDescription = root.Q<Label>("clan-description");
            _clanScore = root.Q<Label>("clan-score");
            _clanRank = root.Q<Label>("clan-rank");
            _clanMembers = root.Q<Label>("clan-members");
            _motdField = root.Q<TextField>("motd-field");
            _motdButton = root.Q<Button>("motd-button");
            _membersList = root.Q<ScrollView>("members-list");
            _activityList = root.Q<ScrollView>("activity-list");
            _leaveButton = root.Q<Button>("leave-button");
            _disbandButton = root.Q<Button>("disband-button");

            root.Q<Button>("create-button").clicked += OnButtonClick_CreateClan;
            root.Q<Button>("search-button").clicked += OnButtonClick_Search;
            _motdButton.clicked += OnButtonClick_SaveMotd;
            _leaveButton.clicked += OnButtonClick_Leave;
            _disbandButton.clicked += OnButtonClick_Disband;

            _coordinator.State.ClanChanged += ClanChangedCallback;
            _coordinator.State.MembersChanged += MembersChangedCallback;
        }

        public void Dispose()
        {
            _coordinator.State.ClanChanged -= ClanChangedCallback;
            _coordinator.State.MembersChanged -= MembersChangedCallback;
        }

        public void Activate()
        {
            Rebuild();
            if (_coordinator.State.IsInClan)
            {
                _ = _window.RunBusyAsync(async () =>
                {
                    await LoadActivityAsync();
                    await LoadClanRankAsync();
                });
            }
        }

        private void OnButtonClick_CreateClan()
        {
            string name = _createName.value;
            string tag = _createTag.value;
            string description = _createDescription.value;
            bool isPublic = _createPublic.value;

            _ = _window.RunBusyAsync(async () =>
            {
                int emblem = Mathf.Abs((name ?? string.Empty).GetHashCode()) % _emblemColors.Length;
                SocialResult result = await _coordinator.CreateClanAsync(name, tag, description, isPublic, emblem);
                if (result.IsSuccess)
                {
                    _createName.SetValueWithoutNotify(string.Empty);
                    _createTag.SetValueWithoutNotify(string.Empty);
                    _createDescription.SetValueWithoutNotify(string.Empty);
                }
            });
        }

        private void OnButtonClick_Search()
        {
            string query = _searchField != null ? _searchField.value : string.Empty;
            _ = _window.RunBusyAsync(async () =>
            {
                SocialResult<ClanSearchPage> result = await _coordinator.SearchClansAsync(query, 0);
                _searchList.Clear();

                if (!result.IsSuccess)
                {
                    _searchEmpty.text = result.Message;
                    _searchEmpty.style.display = DisplayStyle.Flex;
                    return;
                }

                List<ClanSummary> clans = result.Value != null ? result.Value.Clans : null;
                if (clans == null || clans.Count == 0)
                {
                    _searchEmpty.text = "No clans matched that search.";
                    _searchEmpty.style.display = DisplayStyle.Flex;
                    return;
                }

                _searchEmpty.style.display = DisplayStyle.None;
                for (int i = 0; i < clans.Count; i++)
                {
                    ClanSummary summary = clans[i];
                    string subtitle = $"{summary.MemberCount}/{summary.MaxMembers} members  -  {summary.Score:N0} pts  -  level {summary.Level}";
                    VisualElement row = SocialRowFactory.CreateRow($"[{summary.Tag}] {summary.Name}", subtitle, false);

                    ClanSummary captured = summary;
                    bool isFull = captured.MemberCount >= captured.MaxMembers;
                    Button join = SocialRowFactory.AddAction(row, captured.IsPublic ? "Join" : "Request", () =>
                    {
                        _ = _window.RunBusyAsync(async () => await _coordinator.JoinClanAsync(captured.ClanId));
                    });
                    join.SetEnabled(!isFull);

                    _searchList.Add(row);
                }
            });
        }

        private void OnButtonClick_SaveMotd()
        {
            string motd = _motdField != null ? _motdField.value : string.Empty;
            _ = _window.RunBusyAsync(async () => await _coordinator.UpdateClanSettingsAsync(null, motd, null, null));
        }

        private void OnButtonClick_Leave()
        {
            _ = _window.RunBusyAsync(async () => await _coordinator.LeaveClanAsync());
        }

        private void OnButtonClick_Disband()
        {
            _ = _window.RunBusyAsync(async () => await _coordinator.DisbandClanAsync());
        }

        private void Rebuild()
        {
            bool isInClan = _coordinator.State.IsInClan;
            _noneView.EnableInClassList("visible", !isInClan);
            _detailView.EnableInClassList("visible", isInClan);

            if (!isInClan)
            {
                return;
            }

            ClanProfile clan = _coordinator.State.Clan;
            ClanRole myRole = _coordinator.State.MyRole;

            _clanName.text = clan.Name;
            _clanSub.text = $"[{clan.Tag}]   Level {clan.Level}   -   led by {clan.OwnerName}   -   {(clan.IsPublic ? "Public" : "Invite only")}   -   founded {clan.CreatedAtUtc:d}";
            _clanDescription.text = string.IsNullOrEmpty(clan.Description) ? "No description set." : clan.Description;
            _clanScore.text = $"Score {clan.Score:N0}";
            _clanRank.text = _cachedClanRank > 0 ? $"Rank #{_cachedClanRank}" : "Rank -";
            _clanMembers.text = $"{clan.MemberCount}/{clan.MaxMembers} members";
            _emblem.style.backgroundColor = _emblemColors[Mathf.Clamp(clan.EmblemId, 0, _emblemColors.Length - 1)];

            if (_motdField != null && string.IsNullOrEmpty(_motdField.value))
            {
                _motdField.SetValueWithoutNotify(clan.Motd ?? string.Empty);
            }

            bool canEdit = myRole >= ClanRole.Officer;
            _motdField.SetEnabled(canEdit);
            _motdButton.SetEnabled(canEdit);
            _disbandButton.style.display = myRole == ClanRole.Owner ? DisplayStyle.Flex : DisplayStyle.None;

            RebuildMembers();
        }

        private void RebuildMembers()
        {
            _membersList.Clear();

            IReadOnlyList<ClanMember> members = _coordinator.State.Members;
            ClanRole myRole = _coordinator.State.MyRole;
            string myId = _coordinator.PlayerId;

            for (int i = 0; i < members.Count; i++)
            {
                ClanMember member = members[i];
                bool isSelf = string.Equals(member.PlayerId, myId);
                string subtitle = $"{member.Contribution:N0} contributed  -  joined {SocialTime.FromUnixMs(member.JoinedAtMs):d}  -  active {SocialTime.DescribeAge(member.LastActiveMs)}";
                VisualElement row = SocialRowFactory.CreateRow(member.Name ?? "Player", subtitle, isSelf);
                SocialRowFactory.AddRoleBadge(row, member.Role);

                ClanMember captured = member;
                SocialRowFactory.AddAction(row, "Profile", () => _popup.ShowMember(captured));

                if (!isSelf && myRole == ClanRole.Owner)
                {
                    if (captured.Role == ClanRole.Member)
                    {
                        SocialRowFactory.AddAction(row, "Promote", () =>
                        {
                            _ = _window.RunBusyAsync(async () => await _coordinator.SetMemberRoleAsync(captured.PlayerId, ClanRole.Officer));
                        });
                    }
                    else if (captured.Role == ClanRole.Officer)
                    {
                        SocialRowFactory.AddAction(row, "Demote", () =>
                        {
                            _ = _window.RunBusyAsync(async () => await _coordinator.SetMemberRoleAsync(captured.PlayerId, ClanRole.Member));
                        });
                    }

                    SocialRowFactory.AddAction(row, "Make leader", () =>
                    {
                        _ = _window.RunBusyAsync(async () => await _coordinator.TransferOwnershipAsync(captured.PlayerId, captured.Name));
                    });
                }

                if (!isSelf && myRole >= ClanRole.Officer && captured.Role < myRole)
                {
                    SocialRowFactory.AddAction(row, "Kick", () =>
                    {
                        _ = _window.RunBusyAsync(async () => await _coordinator.KickMemberAsync(captured.PlayerId, captured.Name));
                    }, true);
                }

                _membersList.Add(row);
            }
        }

        private async System.Threading.Tasks.Task LoadActivityAsync()
        {
            SocialResult<List<ClanActivityEntry>> result = await _coordinator.GetActivityAsync();
            _activityList.Clear();

            if (!result.IsSuccess || result.Value == null || result.Value.Count == 0)
            {
                Label empty = new Label(result.IsSuccess ? "No activity yet." : result.Message);
                empty.AddToClassList("empty-state");
                _activityList.Add(empty);
                return;
            }

            for (int i = 0; i < result.Value.Count; i++)
            {
                ClanActivityEntry entry = result.Value[i];
                VisualElement row = SocialRowFactory.CreateRow(entry.Text, SocialTime.DescribeAge(entry.TimestampMs), false);
                _activityList.Add(row);
            }
        }

        private async System.Threading.Tasks.Task LoadClanRankAsync()
        {
            SocialResult<LeaderboardPage> result = await _coordinator.GetClanLeaderboardAsync(0);
            if (result.IsSuccess && result.Value != null && result.Value.Self != null)
            {
                _cachedClanRank = result.Value.Self.Rank;
                _clanRank.text = $"Rank #{_cachedClanRank}";
            }
        }

        private void ClanChangedCallback()
        {
            Rebuild();
        }

        private void MembersChangedCallback()
        {
            if (_coordinator.State.IsInClan)
            {
                RebuildMembers();
            }
        }
    }
}
