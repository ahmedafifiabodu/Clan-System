using System;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Owns the social window shell: tab switching, the header, the busy indicator and toasts.
    /// Each tab is a separate controller so this class stays a router rather than a god object.
    /// </summary>
    public class SocialWindowController
    {
        private const string _selectedClass = "selected";
        private const string _visibleClass = "visible";

        /// <summary>
        /// Chat page width below which the voice rail stops being a rail. 268px of rail plus the
        /// 220px minimum the conversation needs leaves no room to split under this.
        /// </summary>
        private const float _chatNarrowBreakpoint = 640f;

        private readonly VisualElement _root;
        private readonly SocialCoordinator _coordinator;
        private readonly PlayerProfilePopup _popup;

        private readonly FriendsTabController _friendsTab;
        private readonly ClanTabController _clanTab;
        private readonly ChatTabController _chatTab;
        private readonly LeaderboardsTabController _leaderboardsTab;
        private readonly NotificationsTabController _notificationsTab;
        private readonly NotificationDockController _notificationDock;
        private readonly NotificationInbox _notificationInbox;
        private readonly VoiceBarController _voiceBar;

        private readonly Button[] _tabButtons;
        private readonly VisualElement[] _pages;

        private readonly Label _headerName;
        private readonly Label _headerMeta;
        private readonly TextField _renameField;
        private readonly VisualElement _busyBar;
        private readonly VisualElement _toast;
        private readonly Label _toastLabel;

        private int _busyCount;
        private IVisualElementScheduledItem _toastTimer;

        public SocialWindowController(VisualElement root, SocialCoordinator coordinator)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));

            _headerName = root.Q<Label>("header-name");
            _headerMeta = root.Q<Label>("header-meta");
            _renameField = root.Q<TextField>("rename-field");
            _busyBar = root.Q<VisualElement>("busy-bar");
            _toast = root.Q<VisualElement>("toast");
            _toastLabel = root.Q<Label>("toast-label");

            _popup = new PlayerProfilePopup(root, coordinator, this);

            _tabButtons = new Button[]
            {
                root.Q<Button>("tab-friends"),
                root.Q<Button>("tab-clan"),
                root.Q<Button>("tab-chat"),
                root.Q<Button>("tab-leaderboards"),
            };

            _pages = new VisualElement[]
            {
                root.Q<VisualElement>("page-friends"),
                root.Q<VisualElement>("page-clan"),
                root.Q<VisualElement>("page-chat"),
                root.Q<VisualElement>("page-leaderboards"),
            };

            _friendsTab = new FriendsTabController(root, coordinator, this, _popup);
            _clanTab = new ClanTabController(root, coordinator, this, _popup);
            _chatTab = new ChatTabController(root, coordinator, this);
            _leaderboardsTab = new LeaderboardsTabController(root, coordinator, this);

            // Notifications left the tab bar: they now live behind a floating button, so the inbox
            // has to exist before the panel that reads from it and the button that counts it.
            _notificationInbox = new NotificationInbox(coordinator.State, coordinator.PlayerId);
            _notificationsTab = new NotificationsTabController(root, coordinator, this, _notificationInbox);
            _notificationDock = new NotificationDockController(root, _notificationInbox, _notificationsTab);
            _voiceBar = new VoiceBarController(root, coordinator, this);

            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int index = i;
                _tabButtons[i].clicked += () => OnButtonClick_SelectTab(index);
            }

            root.Q<Button>("refresh-button").clicked += OnButtonClick_Refresh;
            root.Q<Button>("score-button").clicked += OnButtonClick_PlayMatch;
            root.Q<Button>("rename-button").clicked += OnButtonClick_Rename;

            // USS has no media queries, so the chat page's own width decides its layout. Below the
            // breakpoint the voice rail cannot hold its 268px and the conversation beside it at a
            // usable width, so the rail becomes a strip above the messages instead.
            VisualElement chatSplit = root.Q<VisualElement>("chat-split");
            if (chatSplit != null)
            {
                chatSplit.RegisterCallback<GeometryChangedEvent>(evt =>
                    chatSplit.EnableInClassList("narrow", evt.newRect.width < _chatNarrowBreakpoint));
            }

            _coordinator.StatusReported += StatusReportedCallback;
            _coordinator.State.ClanChanged += ClanChangedCallback;

            SelectTab(1);
            RefreshHeader();
        }

        public void Dispose()
        {
            _coordinator.StatusReported -= StatusReportedCallback;
            _coordinator.State.ClanChanged -= ClanChangedCallback;

            _friendsTab.Dispose();
            _clanTab.Dispose();
            _chatTab.Dispose();
            _leaderboardsTab.Dispose();
            _notificationsTab.Dispose();
            _notificationDock.Dispose();
            _notificationInbox.Dispose();
            _voiceBar.Dispose();
        }

        /// <summary>
        /// Runs a backend call with the busy indicator raised, swallowing cancellation that happens
        /// when the window closes mid-request.
        /// </summary>
        public async Task RunBusyAsync(Func<Task> operation)
        {
            PushBusy();
            try
            {
                await operation();
            }
            catch (OperationCanceledException)
            {
                // The window went away while the request was in flight.
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                ShowToast("Something went wrong. Please try again.", true);
            }
            finally
            {
                PopBusy();
            }
        }

        public void ShowToast(string message, bool isError)
        {
            if (_toast == null || string.IsNullOrEmpty(message))
            {
                return;
            }

            _toastLabel.text = message;
            _toast.EnableInClassList("error", isError);
            _toast.AddToClassList(_visibleClass);

            _toastTimer?.Pause();
            _toastTimer = _toast.schedule.Execute(() => _toast.RemoveFromClassList(_visibleClass)).StartingIn(3200);
        }

        public void RefreshHeader()
        {
            if (_headerName == null)
            {
                return;
            }

            string name = string.IsNullOrEmpty(_coordinator.PlayerName) ? "Player" : _coordinator.PlayerName;
            _headerName.text = name;

            PlayerSocialProfile profile = _coordinator.State.Profile;
            ClanProfile clan = _coordinator.State.Clan;
            string clanText = clan != null ? $"[{clan.Tag}] {clan.Name} - {profile.Role}" : "No clan";
            long score = profile != null ? profile.Score : 0;
            _headerMeta.text = $"{clanText}   |   Score {score:N0}   |   Id {Shorten(_coordinator.PlayerId)}";

            if (_renameField != null && string.IsNullOrEmpty(_renameField.value))
            {
                _renameField.SetValueWithoutNotify(name);
            }
        }

        public void SelectTab(int index)
        {
            for (int i = 0; i < _pages.Length; i++)
            {
                bool isSelected = i == index;
                _pages[i].EnableInClassList(_visibleClass, isSelected);
                _tabButtons[i].EnableInClassList(_selectedClass, isSelected);
            }

            switch (index)
            {
                case 0:
                    _friendsTab.Activate();
                    break;
                case 1:
                    _clanTab.Activate();
                    break;
                case 2:
                    _chatTab.Activate();
                    break;
                case 3:
                    _leaderboardsTab.Activate();
                    break;
            }
        }

        private void OnButtonClick_SelectTab(int index)
        {
            SelectTab(index);
        }

        private void OnButtonClick_Refresh()
        {
            _ = RunBusyAsync(async () =>
            {
                await _coordinator.RefreshSnapshotAsync();
                RefreshHeader();
            });
        }

        private void OnButtonClick_PlayMatch()
        {
            _ = RunBusyAsync(async () =>
            {
                await _coordinator.SubmitDemoScoreAsync();
                RefreshHeader();
            });
        }

        private void OnButtonClick_Rename()
        {
            string requested = _renameField != null ? _renameField.value : string.Empty;
            _ = RunBusyAsync(async () =>
            {
                await _coordinator.SetPlayerNameAsync(requested);
                RefreshHeader();
            });
        }

        private void PushBusy()
        {
            _busyCount++;
            _busyBar?.AddToClassList(_visibleClass);
        }

        private void PopBusy()
        {
            _busyCount = Mathf.Max(0, _busyCount - 1);
            if (_busyCount == 0)
            {
                _busyBar?.RemoveFromClassList(_visibleClass);
            }
        }

        private static string Shorten(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return "-";
            }

            return id.Length <= 8 ? id : id.Substring(0, 8);
        }

        private void StatusReportedCallback(string message, bool isError)
        {
            ShowToast(message, isError);
        }

        private void ClanChangedCallback()
        {
            RefreshHeader();
        }
    }
}
