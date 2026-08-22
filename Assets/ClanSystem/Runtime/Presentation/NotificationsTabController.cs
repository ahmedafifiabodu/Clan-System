using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// The three notification categories - clan invitations, incoming friend requests and pending
    /// join requests - and the tab strip that switches between them.
    ///
    /// Invitations are keyed by an id the server issued to this player, so accepting one on behalf
    /// of somebody else is not expressible through this UI or through the API it calls.
    ///
    /// The categories are the ones the social state already produces; nothing here is synthesised
    /// to fill a tab. Looking at a category marks it read, which is what the badge on the floating
    /// button counts down.
    /// </summary>
    public class NotificationsTabController
    {
        private const string _selectedClass = "selected";
        private const string _visibleClass = "visible";
        private const int _categoryCount = 3;

        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly NotificationInbox _inbox;

        private readonly ScrollView _invitesList;
        private readonly ScrollView _requestsList;
        private readonly ScrollView _joinRequestsList;
        private readonly Label _invitesEmpty;
        private readonly Label _requestsEmpty;
        private readonly Label _joinRequestsEmpty;

        private readonly Button[] _tabs = new Button[_categoryCount];
        private readonly VisualElement[] _categories = new VisualElement[_categoryCount];
        private readonly Label[] _counts = new Label[_categoryCount];

        private NotificationCategory _selected = NotificationCategory.ClanInvites;

        public NotificationsTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window, NotificationInbox inbox)
        {
            _coordinator = coordinator;
            _window = window;
            _inbox = inbox;

            _invitesList = root.Q<ScrollView>("invites-list");
            _requestsList = root.Q<ScrollView>("requests-list");
            _joinRequestsList = root.Q<ScrollView>("joinrequests-list");
            _invitesEmpty = root.Q<Label>("invites-empty");
            _requestsEmpty = root.Q<Label>("requests-empty");
            _joinRequestsEmpty = root.Q<Label>("joinrequests-empty");

            _tabs[0] = root.Q<Button>("notif-tab-invites");
            _tabs[1] = root.Q<Button>("notif-tab-friends");
            _tabs[2] = root.Q<Button>("notif-tab-joins");

            _categories[0] = root.Q<VisualElement>("notif-category-invites");
            _categories[1] = root.Q<VisualElement>("notif-category-friends");
            _categories[2] = root.Q<VisualElement>("notif-category-joins");

            _counts[0] = root.Q<Label>("notif-count-invites");
            _counts[1] = root.Q<Label>("notif-count-friends");
            _counts[2] = root.Q<Label>("notif-count-joins");

            for (int i = 0; i < _categoryCount; i++)
            {
                if (_tabs[i] == null)
                {
                    continue;
                }

                NotificationCategory category = (NotificationCategory)i;
                _tabs[i].clicked += () => OnButtonClick_SelectCategory(category);
            }

            _coordinator.State.NotificationsChanged += NotificationsChangedCallback;
            _coordinator.State.FriendsChanged += NotificationsChangedCallback;
            if (_inbox != null)
            {
                _inbox.Changed += InboxChangedCallback;
            }

            SelectCategory(_selected, false);
        }

        public void Dispose()
        {
            _coordinator.State.NotificationsChanged -= NotificationsChangedCallback;
            _coordinator.State.FriendsChanged -= NotificationsChangedCallback;
            if (_inbox != null)
            {
                _inbox.Changed -= InboxChangedCallback;
            }
        }

        /// <summary>Called when the panel opens: refresh the rows and read the visible category.</summary>
        public void Activate()
        {
            Rebuild();
            SelectCategory(_selected, true);
        }

        /// <summary>
        /// Shows one category. The switch is a class toggle rather than a rebuild, so the fade in
        /// the stylesheet has something continuous to animate and the scroll positions of the other
        /// two categories survive.
        /// </summary>
        public void SelectCategory(NotificationCategory category, bool markSeen)
        {
            _selected = category;
            for (int i = 0; i < _categoryCount; i++)
            {
                bool isSelected = i == (int)category;
                _tabs[i]?.EnableInClassList(_selectedClass, isSelected);
                _categories[i]?.EnableInClassList(_visibleClass, isSelected);
            }

            if (markSeen)
            {
                _inbox?.MarkSeen(category);
            }

            RefreshCounts();
        }

        /// <summary>
        /// Per-tab unread counts. They come from the same inbox as the badge, so a tab and the
        /// button can never disagree about how much is unread.
        /// </summary>
        private void RefreshCounts()
        {
            if (_inbox == null)
            {
                return;
            }

            for (int i = 0; i < _categoryCount; i++)
            {
                Label count = _counts[i];
                if (count == null)
                {
                    continue;
                }

                int unread = _inbox.UnreadCount((NotificationCategory)i);
                count.text = unread > 9 ? "9+" : unread.ToString();
                count.EnableInClassList(_visibleClass, unread > 0);
            }
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

        private void OnButtonClick_SelectCategory(NotificationCategory category)
        {
            SelectCategory(category, true);
        }

        private void NotificationsChangedCallback()
        {
            Rebuild();
            RefreshCounts();
        }

        private void InboxChangedCallback()
        {
            RefreshCounts();
        }
    }
}
