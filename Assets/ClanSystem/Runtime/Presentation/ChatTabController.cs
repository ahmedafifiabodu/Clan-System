using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Text chat over Vivox, for the global channel and the player's own clan channel.
    /// The clan tab is only usable while the server says the player is in a clan, and the transport
    /// refuses the channel anyway without a server-issued token.
    /// </summary>
    public class ChatTabController
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly ICommunicationService _comm;
        private readonly EmojiPickerController _emojiPicker;

        private readonly Button _globalTab;
        private readonly Button _clanTab;
        private readonly ScrollView _list;
        private readonly Label _empty;
        private readonly Label _hint;
        private readonly Label _connection;
        private readonly TextField _input;
        private readonly Button _send;
        private readonly Button _emojiToggle;

        private CommChannelKind _channel = CommChannelKind.Global;
        private bool _isLoading;

        public ChatTabController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window)
        {
            _coordinator = coordinator;
            _window = window;
            _comm = coordinator.Communication;

            _globalTab = root.Q<Button>("chat-tab-global");
            _clanTab = root.Q<Button>("chat-tab-clan");
            _list = root.Q<ScrollView>("chat-list");
            _empty = root.Q<Label>("chat-empty");
            _hint = root.Q<Label>("chat-hint");
            _connection = root.Q<Label>("chat-connection");
            _input = root.Q<TextField>("chat-field");
            _send = root.Q<Button>("chat-send");
            _emojiToggle = root.Q<Button>("chat-emoji-toggle");

            _emojiPicker = new EmojiPickerController(
                root.Q<VisualElement>("emoji-picker"),
                coordinator.Config.EmojiDatabase,
                InsertEmoji);

            _globalTab.clicked += OnButtonClick_SelectGlobal;
            _clanTab.clicked += OnButtonClick_SelectClan;
            _send.clicked += OnButtonClick_Send;
            _emojiToggle.clicked += OnButtonClick_ToggleEmoji;

            _input.maxLength = coordinator.Config.ChatMessageMaxLength;
            _input.RegisterCallback<KeyDownEvent>(SubmitKeyCallback);

            if (_comm != null)
            {
                _comm.MessageReceived += MessageReceivedCallback;
                _comm.StateChanged += TransportStateChangedCallback;
            }

            _coordinator.State.ClanChanged += ClanChangedCallback;
            SelectChannel(CommChannelKind.Global);
        }

        public void Dispose()
        {
            if (_comm != null)
            {
                _comm.MessageReceived -= MessageReceivedCallback;
                _comm.StateChanged -= TransportStateChangedCallback;
            }

            _coordinator.State.ClanChanged -= ClanChangedCallback;
        }

        public void Activate()
        {
            Rebuild();
            _ = LoadHistoryAsync();
        }

        private void OnButtonClick_SelectGlobal()
        {
            SelectChannel(CommChannelKind.Global);
        }

        private void OnButtonClick_SelectClan()
        {
            SelectChannel(CommChannelKind.Clan);
        }

        private void OnButtonClick_ToggleEmoji()
        {
            _emojiPicker.Toggle();
        }

        private void OnButtonClick_Send()
        {
            SendCurrentMessage();
        }

        private void InsertEmoji(string character)
        {
            string current = _input.value ?? string.Empty;
            int limit = _coordinator.Config.ChatMessageMaxLength;
            if (current.Length + character.Length > limit)
            {
                return;
            }

            _input.value = current + character;
            _input.Focus();
        }

        private void SubmitKeyCallback(KeyDownEvent keyEvent)
        {
            if (keyEvent.keyCode == UnityEngine.KeyCode.Return || keyEvent.keyCode == UnityEngine.KeyCode.KeypadEnter)
            {
                SendCurrentMessage();
                keyEvent.StopPropagation();
            }
        }

        private void SendCurrentMessage()
        {
            if (_comm == null)
            {
                return;
            }

            string text = _input != null ? _input.value : string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            // `:fire:` style shorthand resolves to the same character the picker inserts.
            EmojiDatabase database = _coordinator.Config.EmojiDatabase;
            if (database != null)
            {
                text = database.ExpandShortcodes(text);
            }

            int limit = _coordinator.Config.ChatMessageMaxLength;
            if (text.Length > limit)
            {
                text = text.Substring(0, limit);
            }

            CommChannelKind channel = _channel;
            _input.SetValueWithoutNotify(string.Empty);
            _emojiPicker.Hide();

            _ = _window.RunBusyAsync(async () =>
            {
                SocialResult result = await _comm.SendTextAsync(channel, text, _coordinator.Lifetime);
                if (!result.IsSuccess)
                {
                    _window.ShowToast(result.Message, true);
                }
            });
        }

        private void SelectChannel(CommChannelKind channel)
        {
            _channel = channel;
            _globalTab.EnableInClassList("selected", channel == CommChannelKind.Global);
            _clanTab.EnableInClassList("selected", channel == CommChannelKind.Clan);
            _emojiPicker.Hide();
            Rebuild();
            _ = LoadHistoryAsync();
        }

        private async System.Threading.Tasks.Task LoadHistoryAsync()
        {
            if (_comm == null || !_comm.IsLoggedIn)
            {
                return;
            }

            CommChannelKind channel = _channel;
            if (channel == CommChannelKind.Clan && !_coordinator.State.IsInClan)
            {
                return;
            }

            _isLoading = true;
            Rebuild();

            await _comm.GetHistoryAsync(channel, _coordinator.Config.ChatHistoryLimit, _coordinator.Lifetime);

            _isLoading = false;
            if (_channel == channel)
            {
                Rebuild();
            }
        }

        private void Rebuild()
        {
            bool isClanChannel = _channel == CommChannelKind.Clan;
            bool isInClan = _coordinator.State.IsInClan;
            bool isTransportReady = _comm != null && _comm.State == CommConnectionState.Connected;
            bool canSend = isTransportReady && (!isClanChannel || isInClan);

            _send.SetEnabled(canSend);
            _input.SetEnabled(canSend);
            _emojiToggle.SetEnabled(canSend);

            UpdateConnectionLabel();

            if (isClanChannel && !isInClan)
            {
                _hint.text = "Join a clan to use clan chat.";
            }
            else
            {
                _hint.text = $"Up to {_coordinator.Config.ChatMessageMaxLength} characters. Type :fire: or use the emoji picker.";
            }

            _list.Clear();
            IReadOnlyList<CommMessage> messages = _comm != null
                ? _comm.GetBufferedMessages(_channel)
                : new List<CommMessage>();

            for (int i = 0; i < messages.Count; i++)
            {
                _list.Add(SocialRowFactory.CreateChatLine(messages[i]));
            }

            bool hasMessages = messages.Count > 0;
            _empty.style.display = hasMessages ? DisplayStyle.None : DisplayStyle.Flex;

            if (_isLoading)
            {
                _empty.text = "Loading messages...";
            }
            else if (isClanChannel && !isInClan)
            {
                _empty.text = "Clan chat unlocks when you join a clan.";
            }
            else if (!isTransportReady)
            {
                _empty.text = "Chat is offline.";
            }
            else
            {
                _empty.text = "No messages yet. Say hello.";
            }

            _list.schedule.Execute(() => _list.scrollOffset = new UnityEngine.Vector2(0f, float.MaxValue)).StartingIn(16);
        }

        private void UpdateConnectionLabel()
        {
            if (_connection == null)
            {
                return;
            }

            if (_comm == null)
            {
                _connection.text = "OFFLINE";
                _connection.EnableInClassList("online", false);
                return;
            }

            string label;
            switch (_comm.State)
            {
                case CommConnectionState.Connected: label = "CONNECTED"; break;
                case CommConnectionState.Connecting: label = "CONNECTING"; break;
                case CommConnectionState.Recovering: label = "RECONNECTING"; break;
                case CommConnectionState.NotConfigured: label = "NOT CONFIGURED"; break;
                case CommConnectionState.Failed: label = "CONNECTION LOST"; break;
                default: label = "OFFLINE"; break;
            }

            _connection.text = label;
            _connection.tooltip = _comm.StateDetail;
            _connection.EnableInClassList("online", _comm.State == CommConnectionState.Connected);
            _connection.EnableInClassList("error", _comm.State == CommConnectionState.Failed || _comm.State == CommConnectionState.NotConfigured);
        }

        private void MessageReceivedCallback(CommMessage message)
        {
            // A null message signals the clan channel was reset after a membership change.
            if (message == null || message.Channel == _channel)
            {
                Rebuild();
            }
        }

        private void TransportStateChangedCallback()
        {
            Rebuild();
        }

        private void ClanChangedCallback()
        {
            Rebuild();
        }
    }
}
