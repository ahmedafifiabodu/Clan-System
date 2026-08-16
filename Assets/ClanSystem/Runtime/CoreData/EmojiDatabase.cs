using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClanSystem.CoreData
{
    /// <summary>
    /// One emoji: the character actually sent over the wire plus the shortcode used for search and
    /// for the `:name:` typing shorthand.
    /// </summary>
    [Serializable]
    public class EmojiEntry
    {
        [SerializeField] private string _character = string.Empty;
        [SerializeField] private string _shortcode = string.Empty;

        public string Character => _character;
        public string Shortcode => _shortcode;

        public EmojiEntry()
        {
        }

        public EmojiEntry(string character, string shortcode)
        {
            _character = character;
            _shortcode = shortcode;
        }
    }

    /// <summary>
    /// A named group of emoji shown as one tab in the picker.
    /// </summary>
    [Serializable]
    public class EmojiCategory
    {
        [SerializeField] private string _name = "Category";
        [SerializeField] private string _icon = "*";
        [SerializeField] private List<EmojiEntry> _entries = new List<EmojiEntry>();

        public string Name => _name;
        public string Icon => _icon;
        public IReadOnlyList<EmojiEntry> Entries => _entries;

        public EmojiCategory()
        {
        }

        public EmojiCategory(string name, string icon, List<EmojiEntry> entries)
        {
            _name = name;
            _icon = icon;
            _entries = entries;
        }
    }

    /// <summary>
    /// Emoji catalogue used by the picker and by shortcode expansion. Kept as an asset so emoji can
    /// be added or removed without touching a single UI script.
    /// </summary>
    [CreateAssetMenu(fileName = "EmojiDatabase", menuName = "Clan System/Emoji Database", order = 1)]
    public class EmojiDatabase : ScriptableObject
    {
        [SerializeField] private List<EmojiCategory> _categories = new List<EmojiCategory>();

        private Dictionary<string, string> _shortcodeLookup;

        public IReadOnlyList<EmojiCategory> Categories => _categories;

        /// <summary>
        /// Replaces `:shortcode:` occurrences with the emoji character, so players can either pick
        /// from the UI or type the shorthand.
        /// </summary>
        public string ExpandShortcodes(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf(':') < 0)
            {
                return text;
            }

            EnsureLookup();

            System.Text.StringBuilder builder = new System.Text.StringBuilder(text.Length);
            int index = 0;
            while (index < text.Length)
            {
                char character = text[index];
                if (character != ':')
                {
                    builder.Append(character);
                    index++;
                    continue;
                }

                int closing = text.IndexOf(':', index + 1);
                if (closing < 0)
                {
                    builder.Append(text, index, text.Length - index);
                    break;
                }

                string code = text.Substring(index + 1, closing - index - 1);
                string replacement;
                if (code.Length > 0 && _shortcodeLookup.TryGetValue(code, out replacement))
                {
                    builder.Append(replacement);
                    index = closing + 1;
                }
                else
                {
                    builder.Append(character);
                    index++;
                }
            }

            return builder.ToString();
        }

        /// <summary>
        /// Fills the asset with a default set of common emoji. Used by the editor bootstrap so the
        /// picker is populated out of the box.
        /// </summary>
        public void PopulateDefaults()
        {
            _categories = new List<EmojiCategory>
            {
                new EmojiCategory("Smileys", "\U0001F600", new List<EmojiEntry>
                {
                    new EmojiEntry("\U0001F600", "grinning"),
                    new EmojiEntry("\U0001F602", "joy"),
                    new EmojiEntry("\U0001F609", "wink"),
                    new EmojiEntry("\U0001F60A", "blush"),
                    new EmojiEntry("\U0001F60E", "sunglasses"),
                    new EmojiEntry("\U0001F914", "thinking"),
                    new EmojiEntry("\U0001F61C", "tongue"),
                    new EmojiEntry("\U0001F62D", "sob"),
                    new EmojiEntry("\U0001F620", "angry"),
                    new EmojiEntry("\U0001F631", "scream"),
                    new EmojiEntry("\U0001F634", "sleeping"),
                    new EmojiEntry("\U0001F971", "yawn"),
                }),
                new EmojiCategory("Gestures", "\U0001F44B", new List<EmojiEntry>
                {
                    new EmojiEntry("\U0001F44B", "wave"),
                    new EmojiEntry("\U0001F44D", "thumbsup"),
                    new EmojiEntry("\U0001F44E", "thumbsdown"),
                    new EmojiEntry("\U0001F44F", "clap"),
                    new EmojiEntry("\U0001F64C", "raised_hands"),
                    new EmojiEntry("\U0001F64F", "pray"),
                    new EmojiEntry("\U0001F4AA", "muscle"),
                    new EmojiEntry("\U0001F91D", "handshake"),
                    new EmojiEntry("\U0000270C", "victory"),
                    new EmojiEntry("\U0001F918", "horns"),
                }),
                new EmojiCategory("Game", "\U0001F3AE", new List<EmojiEntry>
                {
                    new EmojiEntry("\U0001F3AE", "gamepad"),
                    new EmojiEntry("\U0001F525", "fire"),
                    new EmojiEntry("\U0001F4A5", "boom"),
                    new EmojiEntry("\U00002694", "swords"),
                    new EmojiEntry("\U0001F6E1", "shield"),
                    new EmojiEntry("\U0001F3C6", "trophy"),
                    new EmojiEntry("\U0001F947", "first_place"),
                    new EmojiEntry("\U0001F480", "skull"),
                    new EmojiEntry("\U0001F409", "dragon"),
                    new EmojiEntry("\U0001F43A", "wolf"),
                    new EmojiEntry("\U0001F680", "rocket"),
                    new EmojiEntry("\U00002B50", "star"),
                }),
                new EmojiCategory("Status", "\U00002705", new List<EmojiEntry>
                {
                    new EmojiEntry("\U00002705", "check"),
                    new EmojiEntry("\U0000274C", "cross"),
                    new EmojiEntry("\U000026A0", "warning"),
                    new EmojiEntry("\U0001F440", "eyes"),
                    new EmojiEntry("\U0001F4AC", "speech"),
                    new EmojiEntry("\U0001F514", "bell"),
                    new EmojiEntry("\U000023F0", "alarm"),
                    new EmojiEntry("\U0001F4A4", "afk"),
                    new EmojiEntry("\U00002764", "heart"),
                    new EmojiEntry("\U0001F389", "party"),
                }),
            };

            _shortcodeLookup = null;
        }

        private void EnsureLookup()
        {
            if (_shortcodeLookup != null)
            {
                return;
            }

            _shortcodeLookup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < _categories.Count; i++)
            {
                IReadOnlyList<EmojiEntry> entries = _categories[i].Entries;
                for (int j = 0; j < entries.Count; j++)
                {
                    if (!string.IsNullOrEmpty(entries[j].Shortcode))
                    {
                        _shortcodeLookup[entries[j].Shortcode] = entries[j].Character;
                    }
                }
            }
        }
    }
}
