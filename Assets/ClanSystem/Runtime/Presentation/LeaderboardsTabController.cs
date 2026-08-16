using System.Collections.Generic;
using System.Threading.Tasks;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Player and clan leaderboards. Both pages are produced by Cloud Code from the Leaderboards
    /// service; the client only pages through what the server returns and never ranks anything.
    /// </summary>
    public class LeaderboardsTabController
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;

        private readonly Button _playersTab;
        private readonly Button _clansTab;
        private readonly VisualElement _header;
        private readonly ScrollView _list;
        private readonly Label _empty;
        private readonly Label _pageLabel;
        private readonly Button _previous;
        private readonly Button _next;
        private readonly VisualElement _selfRow;

        private bool _isClanBoard;
        private int _offset;
        private int _total;

        public LeaderboardsTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window)
        {
            _coordinator = coordinator;
            _window = window;

            _playersTab = root.Q<Button>("lb-tab-players");
            _clansTab = root.Q<Button>("lb-tab-clans");
            _header = root.Q<VisualElement>("lb-header");
            _list = root.Q<ScrollView>("lb-list");
            _empty = root.Q<Label>("lb-empty");
            _pageLabel = root.Q<Label>("lb-page");
            _previous = root.Q<Button>("lb-prev");
            _next = root.Q<Button>("lb-next");
            _selfRow = root.Q<VisualElement>("lb-self");

            _playersTab.clicked += OnButtonClick_ShowPlayers;
            _clansTab.clicked += OnButtonClick_ShowClans;
            _previous.clicked += OnButtonClick_PreviousPage;
            _next.clicked += OnButtonClick_NextPage;
        }

        public void Dispose()
        {
        }

        public void Activate()
        {
            SetBoard(_isClanBoard, 0);
        }

        private void OnButtonClick_ShowPlayers()
        {
            SetBoard(false, 0);
        }

        private void OnButtonClick_ShowClans()
        {
            SetBoard(true, 0);
        }

        private void OnButtonClick_PreviousPage()
        {
            int pageSize = _coordinator.Config.LeaderboardPageSize;
            SetBoard(_isClanBoard, UnityEngine.Mathf.Max(0, _offset - pageSize));
        }

        private void OnButtonClick_NextPage()
        {
            int pageSize = _coordinator.Config.LeaderboardPageSize;
            SetBoard(_isClanBoard, _offset + pageSize);
        }

        private void SetBoard(bool isClanBoard, int offset)
        {
            _isClanBoard = isClanBoard;
            _offset = offset;

            _playersTab.EnableInClassList("selected", !isClanBoard);
            _clansTab.EnableInClassList("selected", isClanBoard);
            SocialRowFactory.FillLeaderboardHeader(_header, isClanBoard);

            _ = _window.RunBusyAsync(LoadAsync);
        }

        private async Task LoadAsync()
        {
            SocialResult<LeaderboardPage> result = _isClanBoard
                ? await _coordinator.GetClanLeaderboardAsync(_offset)
                : await _coordinator.GetPlayerLeaderboardAsync(_offset);

            _list.Clear();
            _selfRow.Clear();

            if (!result.IsSuccess)
            {
                _empty.text = result.Message;
                _empty.style.display = DisplayStyle.Flex;
                _pageLabel.text = string.Empty;
                _previous.SetEnabled(false);
                _next.SetEnabled(false);
                return;
            }

            LeaderboardPage page = result.Value;
            List<LeaderboardRow> rows = page != null ? page.Rows : null;
            _total = page != null ? page.Total : 0;

            if (rows == null || rows.Count == 0)
            {
                _empty.text = _isClanBoard
                    ? "No clans have scored yet. Create a clan and play a match."
                    : "No scores yet. Press 'Play a match' to submit one.";
                _empty.style.display = DisplayStyle.Flex;
            }
            else
            {
                _empty.style.display = DisplayStyle.None;
                for (int i = 0; i < rows.Count; i++)
                {
                    _list.Add(SocialRowFactory.CreateLeaderboardRow(rows[i], _isClanBoard));
                }
            }

            int pageSize = _coordinator.Config.LeaderboardPageSize;
            int currentPage = (_offset / UnityEngine.Mathf.Max(1, pageSize)) + 1;
            int pageCount = UnityEngine.Mathf.Max(1, UnityEngine.Mathf.CeilToInt(_total / (float)UnityEngine.Mathf.Max(1, pageSize)));
            _pageLabel.text = $"Page {currentPage} of {pageCount}  -  {_total:N0} entries";
            _previous.SetEnabled(_offset > 0);
            _next.SetEnabled(_offset + pageSize < _total);

            if (page != null && page.Self != null)
            {
                Label caption = new Label(_isClanBoard ? "YOUR CLAN" : "YOUR RANK");
                caption.AddToClassList("section-title");
                _selfRow.Add(caption);
                _selfRow.Add(SocialRowFactory.CreateLeaderboardRow(page.Self, _isClanBoard));
            }
        }
    }
}
