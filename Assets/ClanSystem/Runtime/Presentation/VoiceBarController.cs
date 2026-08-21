using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// The voice rail beside the conversation: connection state, which channel the microphone
    /// transmits into, the live participant list, and the mic/speaker dock.
    ///
    /// Two repaint rates, because the two things being shown change at different speeds. The roster
    /// and its speaking dots are polled on the configured indicator interval; the microphone level
    /// is polled far faster, because a meter that lags behind the player's own voice reads as
    /// broken rather than as smooth.
    /// </summary>
    public class VoiceBarController
    {
        /// <summary>Level meter tick. Fast enough to track speech, cheap enough to ignore.</summary>
        private const int _meterIntervalMs = 50;

        /// <summary>
        /// Meter smoothing per tick, 0 to 1. Raw audio energy is spiky enough to strobe; rising
        /// quickly and falling slowly is what makes a level meter legible.
        /// </summary>
        private const float _meterAttack = 0.6f;

        private const float _meterRelease = 0.18f;

        /// <summary>Width of the meter track in pixels. Must match .mic-meter in the stylesheet.</summary>
        private const float _meterWidth = 34f;

        private readonly SocialCoordinator _coordinator;
        private readonly SocialWindowController _window;
        private readonly ICommunicationService _comm;

        private readonly Label _status;
        private readonly Button _joinGlobal;
        private readonly Button _joinClan;
        private readonly Button _leaveVoice;
        private readonly Button _micToggle;
        private readonly Button _speakerToggle;
        private readonly VisualElement _micMeterFill;
        private readonly ScrollView _participants;
        private readonly Label _participantsEmpty;

        private IVisualElementScheduledItem _pollHandle;
        private IVisualElementScheduledItem _meterHandle;
        private float _meterLevel;
        private bool _isMicLive;

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
            _micMeterFill = root.Q<VisualElement>("mic-meter-fill");
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
            float interval = Mathf.Max(0.1f, coordinator.Config.VoiceIndicatorRefreshSeconds);
            _pollHandle = root.schedule.Execute(RefreshParticipants).Every((long)(interval * 1000f));
            _meterHandle = root.schedule.Execute(RefreshMicrophoneLevel).Every(_meterIntervalMs);

            Refresh();
        }

        public void Dispose()
        {
            _pollHandle?.Pause();
            _meterHandle?.Pause();

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
            bool isError = _comm != null
                && (_comm.State == CommConnectionState.Failed || _comm.State == CommConnectionState.NotConfigured);

            // The rail is narrow, so the pill carries a short state word and the full detail moves
            // into the tooltip rather than wrapping onto a second line.
            if (_comm == null)
            {
                _status.text = "OFFLINE";
            }
            else if (_comm.State == CommConnectionState.NotConfigured)
            {
                _status.text = "NOT SET UP";
            }
            else if (_comm.State == CommConnectionState.Recovering)
            {
                _status.text = "RECONNECTING";
            }
            else if (_comm.State == CommConnectionState.Failed)
            {
                _status.text = "DISCONNECTED";
            }
            else if (isInVoice)
            {
                _status.text = "LIVE";
            }
            else if (isReady)
            {
                _status.text = "READY";
            }
            else
            {
                _status.text = "CONNECTING";
            }

            _status.tooltip = _comm != null ? _comm.StateDetail : string.Empty;
            _status.EnableInClassList("online", isInVoice);
            _status.EnableInClassList("error", isError);

            _joinGlobal.SetEnabled(isReady);
            _joinClan.SetEnabled(isReady && isInClan);
            _joinClan.tooltip = isInClan ? "Talk to your clan" : "Join a clan to use clan voice";
            _leaveVoice.SetEnabled(isInVoice);
            _micToggle.SetEnabled(isReady);
            _speakerToggle.SetEnabled(isReady);

            _joinGlobal.EnableInClassList("selected", isInVoice && _comm.ActiveVoiceChannel.Value == CommChannelKind.Global);
            _joinClan.EnableInClassList("selected", isInVoice && _comm.ActiveVoiceChannel.Value == CommChannelKind.Clan);

            // Icon buttons carry no label, so the state has to be readable from the icon itself:
            // colour plus a slash overlay, with the tooltip naming the action for anyone who hovers.
            bool isMicMuted = _comm != null && _comm.IsMicrophoneMuted;
            bool isSpeakerMuted = _comm != null && _comm.IsSpeakerMuted;

            _micToggle.EnableInClassList("on", !isMicMuted);
            _micToggle.EnableInClassList("off", isMicMuted);
            _micToggle.tooltip = isMicMuted ? "Microphone muted - click to unmute" : "Microphone on - click to mute";

            _speakerToggle.EnableInClassList("on", !isSpeakerMuted);
            _speakerToggle.EnableInClassList("off", isSpeakerMuted);
            _speakerToggle.tooltip = isSpeakerMuted ? "Incoming voice muted - click to unmute" : "Incoming voice on - click to mute";

            if (isMicMuted)
            {
                // Drop the level immediately rather than letting it decay, so the meter cannot
                // still be moving after the player has muted themselves.
                _meterLevel = 0f;
                ApplyMeterLevel();
            }

            RefreshParticipants();
        }

        /// <summary>
        /// Drives the mic meter from the transport's measured audio energy. Nothing here invents a
        /// level: when the transport reports nothing, the meter reads zero.
        /// </summary>
        private void RefreshMicrophoneLevel()
        {
            if (_comm == null)
            {
                return;
            }

            float target = Mathf.Clamp01(_comm.MicrophoneEnergy);
            float rate = target > _meterLevel ? _meterAttack : _meterRelease;
            _meterLevel = Mathf.Lerp(_meterLevel, target, rate);
            if (_meterLevel < 0.005f)
            {
                _meterLevel = 0f;
            }

            ApplyMeterLevel();

            // Outline the button while the transport is actually hearing the player, so the cue
            // survives even at a glance too brief to read a 34px meter.
            bool isLive = _meterLevel > 0.06f && !_comm.IsMicrophoneMuted;
            if (isLive != _isMicLive)
            {
                _isMicLive = isLive;
                _micToggle.EnableInClassList("live", isLive);
            }
        }

        private void ApplyMeterLevel()
        {
            if (_micMeterFill == null)
            {
                return;
            }

            _micMeterFill.style.width = _meterWidth * _meterLevel;
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
