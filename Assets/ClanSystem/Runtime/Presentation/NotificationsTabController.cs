using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Clan invitations, incoming friend requests and pending join requests.
    /// Invitations are keyed by an id the server issued to this player, so accepting one on behalf
    /// of somebody else is not expressible through this UI or through the API it calls.
    /// </summary>
    public class NotificationsTabController
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;

        private readonly ScrollView _invitesList;
        private readonly ScrollView _requestsList;
        private readonly ScrollView _joinRequestsList;
        private readonly Label _invitesEmpty;
        private readonly Label _requestsEmpty;
        private readonly Label _joinRequestsEmpty;

        public NotificationsTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window)
        {
            _coordinator = coordinator;
            _window = window;

            _invitesList = root.Q<ScrollView>("invites-list");
            _requestsList = root.Q<ScrollView>("requests-list");
            _joinRequestsList = root.Q<ScrollView>("joinrequests-list");
            _invitesEmpty = root.Q<Label>("invites-empty");
            _requestsEmpty = root.Q<Label>("requests-empty");
            _joinRequestsEmpty = root.Q<Label>("joinrequests-empty");

            _coordinator.State.NotificationsChanged += NotificationsChangedCallback;
            _coordinator.State.FriendsChanged += NotificationsChangedCallback;
        }

        public void Dispose()
        {
            _coordinator.State.NotificationsChanged -= NotificationsChangedCallback;
            _coordinator.State.FriendsChanged -= NotificationsChangedCallback;
        }

        public void Activate()
        {
            Rebuild();
        }

        private void Rebuild()
        {
            RebuildInvites();
            RebuildFriendRequests();
            RebuildJoinRequests();
        }

        private void RebuildInvites()
        {
            _invitesList.Clear();
            IReadOnlyList<ClanInvite> invites = _coordinator.State.Invites;

            for (int i = 0; i < invites.Count; i++)
            {
                ClanInvite invite = invites[i];
                string title = $"{invite.SenderName} invited you to join [{invite.ClanTag}] {invite.ClanName}";
                string subtitle = $"Expires {invite.ExpiresAtUtc.ToLocalTime():g}";
                VisualElement row = SocialRowFactory.CreateRow(title, subtitle, false);

                ClanInvite captured = invite;
                SocialRowFactory.AddAction(row, "Accept", () =>
                {
                    _ = _window.RunBusyAsync(async () => await _coordinator.RespondToInviteAsync(captured.InviteId, true));
                });
                SocialRowFactory.AddAction(row, "Reject", () =>
                {
                    _ = _window.RunBusyAsync(async () => await _coordinator.RespondToInviteAsync(captured.InviteId, false));
                }, true);

                _invitesList.Add(row);
            }

            _invitesEmpty.style.display = invites.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RebuildFriendRequests()
        {
            _requestsList.Clear();
            IReadOnlyList<FriendEntry> friends = _coordinator.State.Friends;
            int shown = 0;

            for (int i = 0; i < friends.Count; i++)
            {
                FriendEntry entry = friends[i];
                if (entry.Kind != SocialRelationKind.IncomingRequest)
                {
                    continue;
                }

                shown++;
                VisualElement row = SocialRowFactory.CreateRow(entry.Name ?? "Player", "wants to be friends", false);

                FriendEntry captured = entry;
                SocialRowFactory.AddAction(row, "Accept", () =>
                {
                    _ = _window.RunBusyAsync(async () =>
                    {
                        SocialResult result = await _coordinator.Friends.AcceptFriendRequestAsync(captured.PlayerId, _coordinator.Lifetime);
                        _window.ShowToast(result.IsSuccess ? "Friend added." : result.Message, !result.IsSuccess);
                        await _coordinator.RefreshFriendsAsync();
                    });
                });
                SocialRowFactory.AddAction(row, "Decline", () =>
                {
                    _ = _window.RunBusyAsync(async () =>
                    {
                        SocialResult result = await _coordinator.Friends.DeclineFriendRequestAsync(captured.PlayerId, _coordinator.Lifetime);
                        _window.ShowToast(result.IsSuccess ? "Request declined." : result.Message, !result.IsSuccess);
                        await _coordinator.RefreshFriendsAsync();
                    });
                }, true);

                _requestsList.Add(row);
            }

            _requestsEmpty.style.display = shown == 0 ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RebuildJoinRequests()
        {
            _joinRequestsList.Clear();
            IReadOnlyList<ClanJoinRequest> requests = _coordinator.State.JoinRequests;

            for (int i = 0; i < requests.Count; i++)
            {
                ClanJoinRequest request = requests[i];
                string subtitle = string.IsNullOrEmpty(request.Message)
                    ? $"asked to join {SocialTime.DescribeAge(request.CreatedAtMs)}"
                    : request.Message;
                VisualElement row = SocialRowFactory.CreateRow(request.Name ?? "Player", subtitle, false);

                ClanJoinRequest captured = request;
                SocialRowFactory.AddAction(row, "Accept", () =>
                {
                    _ = _window.RunBusyAsync(async () => await _coordinator.HandleJoinRequestAsync(captured.PlayerId, true));
                });
                SocialRowFactory.AddAction(row, "Decline", () =>
                {
                    _ = _window.RunBusyAsync(async () => await _coordinator.HandleJoinRequestAsync(captured.PlayerId, false));
                }, true);

                _joinRequestsList.Add(row);
            }

            _joinRequestsEmpty.style.display = requests.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _joinRequestsEmpty.text = _coordinator.State.MyRole >= ClanRole.Officer
                ? "No pending join requests."
                : "Officers and the leader see join requests here.";
        }

        private void NotificationsChangedCallback()
        {
            Rebuild();
        }
    }
}
