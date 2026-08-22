using System;
using System.Collections.Generic;
using ClanSystem.CoreData;
using ClanSystem.Services;
using UnityEngine;

namespace ClanSystem.Presentation
{
    /// <summary>The three kinds of notification the social state already produces.</summary>
    public enum NotificationCategory
    {
        ClanInvites = 0,
        FriendRequests = 1,
        JoinRequests = 2,
    }

    /// <summary>
    /// Read state over the notifications the server already sends. Nothing here invents a
    /// notification: the items are the clan invites, incoming friend requests and pending join
    /// requests held by <see cref="SocialState"/>, and this class only remembers which of them the
    /// player has already looked at.
    ///
    /// "Unread" is derived rather than stored as a count, so it cannot drift: an item is unread
    /// while its id is absent from the seen set for its category. An accepted invite disappears
    /// from the state and its id is pruned, a new one arrives unseen, and both cases fall out of
    /// the same comparison without anybody maintaining a counter.
    ///
    /// Seen ids persist per player, so re-opening the app does not re-announce what was already
    /// read. They are keyed by player id because one machine hosts several profiles in this demo.
    /// </summary>
    public class NotificationInbox : IDisposable
    {
        private const int _categoryCount = 3;
        private const char _separator = '\n';
        private const string _prefsPrefix = "clansystem.notifications.seen";

        private readonly SocialState _state;
        private readonly string _playerId;
        private readonly HashSet<string>[] _seen = new HashSet<string>[_categoryCount];
        private readonly List<string>[] _ids = new List<string>[_categoryCount];
        private readonly int[] _unread = new int[_categoryCount];

        private bool _isDisposed;

        public NotificationInbox(SocialState state, string playerId)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _playerId = string.IsNullOrEmpty(playerId) ? "anonymous" : playerId;

            for (int i = 0; i < _categoryCount; i++)
            {
                _ids[i] = new List<string>();
                _seen[i] = LoadSeen((NotificationCategory)i);
            }

            _state.NotificationsChanged += StateChangedCallback;
            _state.FriendsChanged += StateChangedCallback;
            Recalculate();
        }

        /// <summary>Raised when a count changed - a new item arrived, or one was read or handled.</summary>
        public event Action Changed;

        public int TotalUnread
        {
            get
            {
                int total = 0;
                for (int i = 0; i < _categoryCount; i++)
                {
                    total += _unread[i];
                }

                return total;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _state.NotificationsChanged -= StateChangedCallback;
            _state.FriendsChanged -= StateChangedCallback;
        }

        public int UnreadCount(NotificationCategory category)
        {
            return _unread[(int)category];
        }

        public int Count(NotificationCategory category)
        {
            return _ids[(int)category].Count;
        }

        /// <summary>
        /// Marks everything currently in a category as read. Called when the player looks at that
        /// category - opening the panel on it, or switching to it - which is the only thing that
        /// can honestly be called "read" in a UI with no per-row acknowledgement.
        /// </summary>
        public void MarkSeen(NotificationCategory category)
        {
            int index = (int)category;
            if (_unread[index] == 0)
            {
                return;
            }

            List<string> ids = _ids[index];
            for (int i = 0; i < ids.Count; i++)
            {
                _seen[index].Add(ids[i]);
            }

            _unread[index] = 0;
            SaveSeen(category);
            Changed?.Invoke();
        }

        /// <summary>
        /// Rebuilds the id lists from the current state and re-derives every unread count. Seen ids
        /// that no longer correspond to a live item are dropped here, which is what stops the set
        /// growing for the lifetime of the profile.
        /// </summary>
        private void Recalculate()
        {
            bool hasChanged = false;

            for (int i = 0; i < _categoryCount; i++)
            {
                NotificationCategory category = (NotificationCategory)i;
                List<string> ids = _ids[i];
                ids.Clear();
                CollectIds(category, ids);

                int unread = 0;
                for (int idIndex = 0; idIndex < ids.Count; idIndex++)
                {
                    if (!_seen[i].Contains(ids[idIndex]))
                    {
                        unread++;
                    }
                }

                if (unread != _unread[i])
                {
                    _unread[i] = unread;
                    hasChanged = true;
                }

                if (PruneSeen(i, ids))
                {
                    SaveSeen(category);
                }
            }

            if (hasChanged)
            {
                Changed?.Invoke();
            }
        }

        private void CollectIds(NotificationCategory category, List<string> into)
        {
            switch (category)
            {
                case NotificationCategory.ClanInvites:
                {
                    IReadOnlyList<ClanInvite> invites = _state.Invites;
                    for (int i = 0; i < invites.Count; i++)
                    {
                        string id = invites[i].InviteId;
                        if (!string.IsNullOrEmpty(id))
                        {
                            into.Add(id);
                        }
                    }

                    break;
                }

                case NotificationCategory.FriendRequests:
                {
                    IReadOnlyList<FriendEntry> friends = _state.Friends;
                    for (int i = 0; i < friends.Count; i++)
                    {
                        FriendEntry entry = friends[i];
                        if (entry.Kind == SocialRelationKind.IncomingRequest && !string.IsNullOrEmpty(entry.PlayerId))
                        {
                            into.Add(entry.PlayerId);
                        }
                    }

                    break;
                }

                case NotificationCategory.JoinRequests:
                {
                    IReadOnlyList<ClanJoinRequest> requests = _state.JoinRequests;
                    for (int i = 0; i < requests.Count; i++)
                    {
                        string id = requests[i].PlayerId;
                        if (!string.IsNullOrEmpty(id))
                        {
                            into.Add(id);
                        }
                    }

                    break;
                }
            }
        }

        private bool PruneSeen(int index, List<string> liveIds)
        {
            HashSet<string> seen = _seen[index];
            if (seen.Count == 0)
            {
                return false;
            }

            // A join request declined and re-sent by the same player reuses its id, so pruning is
            // what lets the second one count as unread again.
            List<string> stale = null;
            foreach (string id in seen)
            {
                if (!liveIds.Contains(id))
                {
                    stale ??= new List<string>();
                    stale.Add(id);
                }
            }

            if (stale == null)
            {
                return false;
            }

            for (int i = 0; i < stale.Count; i++)
            {
                seen.Remove(stale[i]);
            }

            return true;
        }

        private HashSet<string> LoadSeen(NotificationCategory category)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            string stored = PlayerPrefs.GetString(PrefsKey(category), string.Empty);
            if (string.IsNullOrEmpty(stored))
            {
                return set;
            }

            string[] parts = stored.Split(_separator);
            for (int i = 0; i < parts.Length; i++)
            {
                if (!string.IsNullOrEmpty(parts[i]))
                {
                    set.Add(parts[i]);
                }
            }

            return set;
        }

        private void SaveSeen(NotificationCategory category)
        {
            HashSet<string> set = _seen[(int)category];
            PlayerPrefs.SetString(PrefsKey(category), set.Count == 0 ? string.Empty : string.Join(_separator.ToString(), set));
        }

        private string PrefsKey(NotificationCategory category)
        {
            return $"{_prefsPrefix}.{_playerId}.{(int)category}";
        }

        private void StateChangedCallback()
        {
            Recalculate();
        }
    }
}
