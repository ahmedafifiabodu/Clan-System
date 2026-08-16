using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Small profile card shown when a player row is clicked. Offers only the actions the local
    /// player could plausibly perform; the server is still the one that decides.
    /// </summary>
    public class PlayerProfilePopup
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;

        private readonly VisualElement _backdrop;
        private readonly Label _name;
        private readonly Label _meta;
        private readonly Label _clan;
        private readonly VisualElement _actions;

        public PlayerProfilePopup(VisualElement root, SocialCoordinator coordinator, SocialWindowController window)
        {
            _coordinator = coordinator;
            _window = window;

            _backdrop = root.Q<VisualElement>("profile-popup");
            _name = root.Q<Label>("popup-name");
            _meta = root.Q<Label>("popup-meta");
            _clan = root.Q<Label>("popup-clan");
            _actions = root.Q<VisualElement>("popup-actions");

            root.Q<Button>("popup-close").clicked += OnButtonClick_Close;
        }

        public void ShowMember(ClanMember member)
        {
            _name.text = member.Name ?? "Player";
            _meta.text = $"{member.Role}  -  {member.Contribution:N0} contributed  -  active {SocialTime.DescribeAge(member.LastActiveMs)}";
            _clan.text = $"Joined {SocialTime.FromUnixMs(member.JoinedAtMs):d}   |   Id {member.PlayerId}";

            _actions.Clear();
            bool isSelf = string.Equals(member.PlayerId, _coordinator.PlayerId);
            ClanRole myRole = _coordinator.State.MyRole;

            if (!isSelf && myRole >= ClanRole.Officer && member.Role < myRole)
            {
                AddAction("Kick from clan", () =>
                {
                    Hide();
                    _ = _window.RunBusyAsync(async () => await _coordinator.KickMemberAsync(member.PlayerId, member.Name));
                }, true);
            }

            if (!isSelf && myRole == ClanRole.Owner)
            {
                AddAction("Transfer leadership", () =>
                {
                    Hide();
                    _ = _window.RunBusyAsync(async () => await _coordinator.TransferOwnershipAsync(member.PlayerId, member.Name));
                });
            }

            if (!isSelf && _coordinator.IsFriendsReady)
            {
                AddAction("Block", () =>
                {
                    Hide();
                    _ = _window.RunBusyAsync(async () =>
                    {
                        SocialResult result = await _coordinator.Friends.BlockPlayerAsync(member.PlayerId, _coordinator.Lifetime);
                        _window.ShowToast(result.IsSuccess ? "Player blocked." : result.Message, !result.IsSuccess);
                    });
                }, true);
            }

            Show();
        }

        public void ShowFriend(FriendEntry friend)
        {
            _name.text = friend.Name ?? "Player";
            _meta.text = $"{(friend.IsOnline ? "Online" : "Offline")}  -  {friend.Score:N0} pts";
            _clan.text = friend.IsInClan ? $"Clan [{friend.ClanTag}] {friend.ClanName}" : "Not in a clan";

            _actions.Clear();

            bool canInvite = _coordinator.State.IsInClan
                && _coordinator.State.MyRole >= ClanRole.Officer
                && !string.Equals(friend.ClanId, _coordinator.State.Clan.ClanId);

            if (canInvite)
            {
                AddAction("Invite to clan", () =>
                {
                    Hide();
                    _ = _window.RunBusyAsync(async () => await _coordinator.InvitePlayerAsync(friend.PlayerId, friend.Name));
                });
            }

            if (friend.Kind == SocialRelationKind.Friend)
            {
                AddAction("Remove friend", () =>
                {
                    Hide();
                    _ = _window.RunBusyAsync(async () =>
                    {
                        SocialResult result = await _coordinator.Friends.RemoveFriendAsync(friend.PlayerId, _coordinator.Lifetime);
                        _window.ShowToast(result.IsSuccess ? "Friend removed." : result.Message, !result.IsSuccess);
                        await _coordinator.RefreshFriendsAsync();
                    });
                }, true);
            }

            AddAction("Block", () =>
            {
                Hide();
                _ = _window.RunBusyAsync(async () =>
                {
                    SocialResult result = await _coordinator.Friends.BlockPlayerAsync(friend.PlayerId, _coordinator.Lifetime);
                    _window.ShowToast(result.IsSuccess ? "Player blocked." : result.Message, !result.IsSuccess);
                    await _coordinator.RefreshFriendsAsync();
                });
            }, true);

            Show();
        }

        public void Hide()
        {
            _backdrop.RemoveFromClassList("visible");
        }

        private void Show()
        {
            _backdrop.AddToClassList("visible");
        }

        private void AddAction(string text, System.Action callback, bool isDanger = false)
        {
            Button button = new Button(callback) { text = text };
            button.AddToClassList("button");
            if (isDanger)
            {
                button.AddToClassList("danger");
            }

            _actions.Add(button);
        }

        private void OnButtonClick_Close()
        {
            Hide();
        }
    }
}
