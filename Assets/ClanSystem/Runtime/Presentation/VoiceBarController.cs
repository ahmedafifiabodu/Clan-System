using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Voice controls: connection state, which channel the microphone transmits into, mic and
    /// speaker toggles, and the live participant list with speaking indicators and per-player mute.
    /// Speaking state is polled on a light schedule because audio energy changes far faster than the
    /// UI needs to repaint.
    /// </summary>
    public class VoiceBarController
    {
        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly ICommunicationService _comm;

        private readonly Label _status;
        private readonly Button _joinGlobal;
        private readonly Button _joinClan;
        private readonly Button _leaveVoice;
        private readonly Button _micToggle;
        private readonly Button _speakerToggle;
        private readonly ScrollView _participants;
        private readonly Label _participantsEmpty;

        private IVisualElementScheduledItem _pollHandle;

        public VoiceBarController(VisualElement root, SocialCoordinator coordinator, SocialWindowController window)
        {
            _coordinator = coordinator;
            _window = window;
            _comm = coordinator.Communication;

            _status = root.Q<Label>("voice-status");
            _joinGlobal = root.Q<Button>("voice-join-global");
            _joinClan = root.Q<Button>("voice-join-clan");
            _leaveVoice = root.Q<Button>("voice-leave");
            _micToggle = root.Q<Button>("voice-mic");
            _speakerToggle = root.Q<Button>("voice-speaker");
            _participants = root.Q<ScrollView>("voice-participants");
            _participantsEmpty = root.Q<Label>("voice-participants-empty");

            _joinGlobal.clicked += OnButtonClick_JoinGlobalVoice;
            _joinClan.clicked += OnButtonClick_JoinClanVoice;
            _leaveVoice.clicked += OnButtonClick_LeaveVoice;
            _micToggle.clicked += OnButtonClick_ToggleMicrophone;
            _speakerToggle.clicked += OnButtonClick_ToggleSpeaker;

            if (_comm != null)
            {
                _comm.StateChanged += TransportStateChangedCallback;
                _comm.ParticipantsChanged += ParticipantsChangedCallback;
            }

            _coordinator.State.ClanChanged += ClanChangedCallback;

            // Speaking indicators need a steady repaint while voice is active.
            float interval = UnityEngine.Mathf.Max(0.1f, coordinator.Config.VoiceIndicatorRefreshSeconds);
            _pollHandle = root.schedule.Execute(RefreshParticipants).Every((long)(interval * 1000f));

            Refresh();
        }

        public void Dispose()
        {
            _pollHandle?.Pause();

            if (_comm != null)
            {
                _comm.StateChanged -= TransportStateChangedCallback;
                _comm.ParticipantsChanged -= ParticipantsChangedCallback;
            }

            _coordinator.State.ClanChanged -= ClanChangedCallback;
        }

        private void OnButtonClick_JoinGlobalVoice()
        {
            JoinVoice(CommChannelKind.Global);
        }

        private void OnButtonClick_JoinClanVoice()
        {
            JoinVoice(CommChannelKind.Clan);
        }

        private void JoinVoice(CommChannelKind channel)
        {
            if (_comm == null)
            {
                return;
            }

            _ = _window.RunBusyAsync(async () =>
            {
                SocialResult result = await _comm.JoinVoiceAsync(channel, _coordinator.Lifetime);
                if (!result.IsSuccess)
                {
                    _window.ShowToast(result.Message, true);
                }
                else
                {
                    _window.ShowToast(channel == CommChannelKind.Clan ? "Joined clan voice." : "Joined global voice.", false);
                }

                Refresh();
            });
        }

        private void OnButtonClick_LeaveVoice()
        {
            if (_comm == null || !_comm.ActiveVoiceChannel.HasValue)
            {
                return;
            }

            CommChannelKind channel = _comm.ActiveVoiceChannel.Value;
            _ = _window.RunBusyAsync(async () =>
            {
                await _comm.LeaveVoiceAsync(channel, _coordinator.Lifetime);
                _window.ShowToast("Left voice channel.", false);
                Refresh();
            });
        }

        private void OnButtonClick_ToggleMicrophone()
        {
            if (_comm == null)
            {
                return;
            }

            _comm.SetMicrophoneMuted(!_comm.IsMicrophoneMuted);
            Refresh();
        }

        private void OnButtonClick_ToggleSpeaker()
        {
            if (_comm == null)
            {
                return;
            }

            _comm.SetSpeakerMuted(!_comm.IsSpeakerMuted);
            Refresh();
        }

        private void Refresh()
        {
            bool isReady = _comm != null && _comm.State == CommConnectionState.Connected;
            bool isInClan = _coordinator.State.IsInClan;
            bool isInVoice = _comm != null && _comm.ActiveVoiceChannel.HasValue;

            if (_comm == null)
            {
                _status.text = "VOICE OFFLINE";
            }
            else if (_comm.State == CommConnectionState.NotConfigured)
            {
                _status.text = "VOICE NOT CONFIGURED";
            }
            else if (_comm.State == CommConnectionState.Recovering)
            {
                _status.text = "VOICE RECONNECTING";
            }
            else if (_comm.State == CommConnectionState.Failed)
            {
                _status.text = "VOICE DISCONNECTED";
            }
            else if (isInVoice)
            {
                _status.text = _comm.ActiveVoiceChannel.Value == CommChannelKind.Clan
                    ? "IN CLAN VOICE"
                    : "IN GLOBAL VOICE";
            }
            else if (isReady)
            {
                _status.text = "VOICE READY";
            }
            else
            {
                _status.text = "VOICE CONNECTING";
            }

            _status.tooltip = _comm != null ? _comm.StateDetail : string.Empty;
            _status.EnableInClassList("online", isInVoice);
            _status.EnableInClassList("error", _comm != null && (_comm.State == CommConnectionState.Failed || _comm.State == CommConnectionState.NotConfigured));

            _joinGlobal.SetEnabled(isReady);
            _joinClan.SetEnabled(isReady && isInClan);
            _leaveVoice.SetEnabled(isInVoice);
            _micToggle.SetEnabled(isReady);
            _speakerToggle.SetEnabled(isReady);

            bool isMicMuted = _comm != null && _comm.IsMicrophoneMuted;
            bool isSpeakerMuted = _comm != null && _comm.IsSpeakerMuted;
            _micToggle.text = isMicMuted ? "Mic off" : "Mic on";
            _speakerToggle.text = isSpeakerMuted ? "Sound off" : "Sound on";
            _micToggle.EnableInClassList("danger", isMicMuted);
            _speakerToggle.EnableInClassList("danger", isSpeakerMuted);

            _joinGlobal.EnableInClassList("selected", isInVoice && _comm.ActiveVoiceChannel.Value == CommChannelKind.Global);
            _joinClan.EnableInClassList("selected", isInVoice && _comm.ActiveVoiceChannel.Value == CommChannelKind.Clan);

            RefreshParticipants();
        }

        private void RefreshParticipants()
        {
            if (_comm == null || !_comm.ActiveVoiceChannel.HasValue)
            {
                _participants.Clear();
                _participantsEmpty.style.display = DisplayStyle.Flex;
                _participantsEmpty.text = "Join a voice channel to see who is connected.";
                return;
            }

            CommChannelKind channel = _comm.ActiveVoiceChannel.Value;
            IReadOnlyList<CommParticipant> people = _comm.GetParticipants(channel);

            _participantsEmpty.style.display = people.Count == 0 ? DisplayStyle.Flex : DisplayStyle.None;
            _participantsEmpty.text = "Nobody else is here yet.";

            // Rebuild only when the roster changes; otherwise update speaking state in place so the
            // indicator does not flicker.
            if (_participants.childCount != people.Count)
            {
                _participants.Clear();
                for (int i = 0; i < people.Count; i++)
                {
                    _participants.Add(BuildParticipantRow(people[i]));
                }

                return;
            }

            for (int i = 0; i < people.Count; i++)
            {
                VisualElement row = _participants[i];
                CommParticipant participant = people[i];
                row.EnableInClassList("speaking", participant.IsSpeaking);

                VisualElement indicator = row.Q<VisualElement>("speaking-dot");
                indicator?.EnableInClassList("active", participant.IsSpeaking);

                Button mute = row.Q<Button>("participant-mute");
                if (mute != null && !participant.IsSelf)
                {
                    mute.text = _comm.IsPlayerMuted(participant.PlayerId) ? "Unmute" : "Mute";
                }
            }
        }

        private VisualElement BuildParticipantRow(CommParticipant participant)
        {
            VisualElement row = new VisualElement();
            row.AddToClassList("row");
            row.AddToClassList("voice-row");
            row.EnableInClassList("speaking", participant.IsSpeaking);

            VisualElement dot = new VisualElement();
            dot.name = "speaking-dot";
            dot.AddToClassList("speaking-dot");
            dot.EnableInClassList("active", participant.IsSpeaking);
            row.Add(dot);

            VisualElement main = new VisualElement();
            main.AddToClassList("row-main");
            Label name = new Label(participant.IsSelf
                ? (participant.DisplayName ?? "You") + " (you)"
                : participant.DisplayName ?? participant.PlayerId);
            name.AddToClassList("row-title");
            main.Add(name);
            row.Add(main);

            VisualElement actions = new VisualElement();
            actions.name = "row-actions";
            actions.AddToClassList("row-actions");
            row.Add(actions);

            if (!participant.IsSelf)
            {
                string playerId = participant.PlayerId;

                Button mute = new Button(() =>
                {
                    bool isMuted = _comm.IsPlayerMuted(playerId);
                    _comm.SetPlayerMuted(playerId, !isMuted);
                    RefreshParticipants();
                })
                {
                    name = "participant-mute",
                    text = _comm.IsPlayerMuted(playerId) ? "Unmute" : "Mute",
                };
                mute.AddToClassList("button");
                mute.AddToClassList("mini");
                actions.Add(mute);

                SliderInt volume = new SliderInt(-50, 50) { value = participant.LocalVolume };
                volume.AddToClassList("volume-slider");
                volume.tooltip = "Volume";
                volume.RegisterValueChangedCallback(evt => _comm.SetPlayerVolume(playerId, evt.newValue));
                actions.Add(volume);
            }

            return row;
        }

        private void TransportStateChangedCallback()
        {
            Refresh();
        }

        private void ParticipantsChangedCallback(CommChannelKind channel)
        {
            RefreshParticipants();
        }

        private void ClanChangedCallback()
        {
            Refresh();
        }
    }
}
