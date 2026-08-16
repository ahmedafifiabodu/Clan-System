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

        private readonly VisualElement _root;
        private readonly SocialCoordinator _coordinator;
        private readonly PlayerProfilePopup _popup;

        private readonly FriendsTabController _friendsTab;
        private readonly ClanTabController _clanTab;
        private readonly ChatTabController _chatTab;
        private readonly LeaderboardsTabController _leaderboardsTab;
        private readonly NotificationsTabController _notificationsTab;
        private readonly VoiceBarController _voiceBar;

        private readonly Button[] _tabButtons;
        private readonly VisualElement[] _pages;

        private readonly Label _headerName;
        private readonly Label _headerMeta;
        private readonly TextField _renameField;
        private readonly VisualElement _busyBar;
        private readonly VisualElement _toast;
        private readonly Label _toastLabel;
        private readonly Button _notificationsButton;

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
                root.Q<Button>("tab-notifications"),
            };

            _pages = new VisualElement[]
            {
                root.Q<VisualElement>("page-friends"),
                root.Q<VisualElement>("page-clan"),
                root.Q<VisualElement>("page-chat"),
                root.Q<VisualElement>("page-leaderboards"),
                root.Q<VisualElement>("page-notifications"),
            };

            _notificationsButton = _tabButtons[4];

            _friendsTab = new FriendsTabController(root, coordinator, this, _popup);
            _clanTab = new ClanTabController(root, coordinator, this, _popup);
            _chatTab = new ChatTabController(root, coordinator, this);
            _leaderboardsTab = new LeaderboardsTabController(root, coordinator, this);
            _notificationsTab = new NotificationsTabController(root, coordinator, this);
            _voiceBar = new VoiceBarController(root, coordinator, this);

            for (int i = 0; i < _tabButtons.Length; i++)
            {
                int index = i;
                _tabButtons[i].clicked += () => OnButtonClick_SelectTab(index);
            }

            root.Q<Button>("refresh-button").clicked += OnButtonClick_Refresh;
            root.Q<Button>("score-button").clicked += OnButtonClick_PlayMatch;
            root.Q<Button>("rename-button").clicked += OnButtonClick_Rename;

            _coordinator.StatusReported += StatusReportedCallback;
            _coordinator.State.ClanChanged += ClanChangedCallback;
            _coordinator.State.NotificationsChanged += NotificationsChangedCallback;

            SelectTab(1);
            RefreshHeader();
        }

        public void Dispose()
        {
            _coordinator.StatusReported -= StatusReportedCallback;
            _coordinator.State.ClanChanged -= ClanChangedCallback;
            _coordinator.State.NotificationsChanged -= NotificationsChangedCallback;

            _friendsTab.Dispose();
            _clanTab.Dispose();
            _chatTab.Dispose();
            _leaderboardsTab.Dispose();
            _notificationsTab.Dispose();
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
                case 4:
                    _notificationsTab.Activate();
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

        private void NotificationsChangedCallback()
        {
            int pending = _coordinator.State.PendingNotificationCount;
            _notificationsButton.text = pending > 0 ? $"Notifications ({pending})" : "Notifications";
        }
    }
}
