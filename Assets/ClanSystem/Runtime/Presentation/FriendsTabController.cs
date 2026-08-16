using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Friends tab: the Friends service social graph on the left, the caller's clan roster on the
    /// right. "Invite to clan" is only offered when the local player actually outranks a member,
    /// but the server re-checks the same rule.
    /// </summary>
    public class FriendsTabController
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly PlayerProfilePopup _popup;

        private readonly ScrollView _friendsList;
        private readonly ScrollView _clanmatesList;
        private readonly Label _friendsEmpty;
        private readonly Label _clanLabel;
        private readonly TextField _addField;

        public FriendsTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window, PlayerProfilePopup popup)
        {
            _coordinator = coordinator;
            _window = window;
            _popup = popup;

            _friendsList = root.Q<ScrollView>("friends-list");
            _clanmatesList = root.Q<ScrollView>("clanmates-list");
            _friendsEmpty = root.Q<Label>("friends-empty");
            _clanLabel = root.Q<Label>("friends-clan-label");
            _addField = root.Q<TextField>("friend-name-field");

            root.Q<Button>("friend-add-button").clicked += OnButtonClick_AddFriend;

            _coordinator.State.FriendsChanged += FriendsChangedCallback;
            _coordinator.State.MembersChanged += MembersChangedCallback;
            _coordinator.State.ClanChanged += MembersChangedCallback;
        }

        public void Dispose()
        {
            _coordinator.State.FriendsChanged -= FriendsChangedCallback;
            _coordinator.State.MembersChanged -= MembersChangedCallback;
            _coordinator.State.ClanChanged -= MembersChangedCallback;
        }

        public void Activate()
        {
            RebuildFriends();
            RebuildClanmates();

            if (!_coordinator.IsFriendsReady)
            {
                return;
            }

            _ = _window.RunBusyAsync(async () => await _coordinator.RefreshFriendsAsync());
        }

        private void OnButtonClick_AddFriend()
        {
            string name = _addField != null ? _addField.value : string.Empty;
            _ = _window.RunBusyAsync(async () =>
            {
                SocialResult result = await _coordinator.Friends.AddFriendByNameAsync(name, _coordinator.Lifetime);
                _window.ShowToast(result.IsSuccess ? "Friend request sent." : result.Message, !result.IsSuccess);
                if (result.IsSuccess)
                {
                    _addField.SetValueWithoutNotify(string.Empty);
                    await _coordinator.RefreshFriendsAsync();
                }
            });
        }

        private void RebuildFriends()
        {
            _friendsList.Clear();
            IReadOnlyList<FriendEntry> friends = _coordinator.State.Friends;

            int shown = 0;
            for (int i = 0; i < friends.Count; i++)
            {
                FriendEntry friend = friends[i];
                if (friend.Kind != SocialRelationKind.Friend)
                {
                    continue;
                }

                shown++;
                string subtitle = friend.IsInClan ? $"[{friend.ClanTag}] {friend.ClanName}  -  {friend.Score:N0} pts" : $"No clan  -  {friend.Score:N0} pts";
                VisualElement row = SocialRowFactory.CreatePresenceRow(friend.Name ?? "Player", subtitle, friend.IsOnline);

                FriendEntry captured = friend;
                SocialRowFactory.AddAction(row, "Profile", () => _popup.ShowFriend(captured));

                bool canInvite = _coordinator.State.IsInClan
                    && _coordinator.State.MyRole >= ClanRole.Officer
                    && !string.Equals(captured.ClanId, _coordinator.State.Clan.ClanId);

                if (canInvite)
                {
                    SocialRowFactory.AddAction(row, "Invite", () =>
                    {
                        _ = _window.RunBusyAsync(async () => await _coordinator.InvitePlayerAsync(captured.PlayerId, captured.Name));
                    });
                }

                _friendsList.Add(row);
            }

            if (_friendsEmpty != null)
            {
                _friendsEmpty.style.display = shown == 0 ? DisplayStyle.Flex : DisplayStyle.None;
                _friendsEmpty.text = _coordinator.IsFriendsReady
                    ? "No friends yet. Add one by name above."
                    : "Friends service unavailable. Enable Friends in the Unity Dashboard.";
            }
        }

        private void RebuildClanmates()
        {
            _clanmatesList.Clear();

            if (!_coordinator.State.IsInClan)
            {
                _clanLabel.text = "You are not in a clan.";
                return;
            }

            ClanProfile clan = _coordinator.State.Clan;
            _clanLabel.text = $"[{clan.Tag}] {clan.Name} - {clan.MemberCount}/{clan.MaxMembers} members";

            IReadOnlyList<ClanMember> members = _coordinator.State.Members;
            for (int i = 0; i < members.Count; i++)
            {
                ClanMember member = members[i];
                bool isSelf = string.Equals(member.PlayerId, _coordinator.PlayerId);
                string subtitle = $"{member.Contribution:N0} contributed  -  active {SocialTime.DescribeAge(member.LastActiveMs)}";
                VisualElement row = SocialRowFactory.CreateRow(member.Name ?? "Player", subtitle, isSelf);
                SocialRowFactory.AddRoleBadge(row, member.Role);

                ClanMember captured = member;
                SocialRowFactory.AddAction(row, "Profile", () => _popup.ShowMember(captured));
                _clanmatesList.Add(row);
            }
        }

        private void FriendsChangedCallback()
        {
            RebuildFriends();
        }

        private void MembersChangedCallback()
        {
            RebuildClanmates();
        }
    }
}
