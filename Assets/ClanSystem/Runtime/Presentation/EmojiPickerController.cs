using System;
using System.Collections.Generic;
using ClanSystem.CoreData;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Emoji picker built entirely from the <see cref="EmojiDatabase"/> asset. No emoji character
    /// appears in this file, so the catalogue can change without touching UI code.
    /// </summary>
    public class EmojiPickerController
    {
        private readonly EmojiDatabase _database;
        private readonly VisualElement _root;
        private readonly VisualElement _categoryBar;
        private readonly ScrollView _grid;
        private readonly Action<string> _onEmojiChosen;

        private int _selectedCategory;

        public EmojiPickerController(VisualElement root, EmojiDatabase database, Action<string> onEmojiChosen)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _database = database;
            _onEmojiChosen = onEmojiChosen;

            _categoryBar = root.Q<VisualElement>("emoji-categories");
            _grid = root.Q<ScrollView>("emoji-grid");

            Build();
            Hide();
        }

        public bool IsVisible { get; private set; }

        public void Toggle()
        {
            if (IsVisible)
            {
                Hide();
            }
            else
            {
                Show();
            }
        }

        public void Show()
        {
            if (_database == null || _database.Categories.Count == 0)
            {
                return;
            }

            IsVisible = true;
            _root.AddToClassList("visible");
        }

        public void Hide()
        {
            IsVisible = false;
            _root.RemoveFromClassList("visible");
        }

        private void Build()
        {
            _categoryBar.Clear();
            _grid.Clear();

            if (_database == null || _database.Categories.Count == 0)
            {
                Label empty = new Label("No emoji configured.");
                empty.AddToClassList("empty-state");
                _grid.Add(empty);
                return;
            }

            IReadOnlyList<EmojiCategory> categories = _database.Categories;
            for (int i = 0; i < categories.Count; i++)
            {
                int index = i;
                Button tab = new Button(() => SelectCategory(index)) { text = categories[i].Icon };
                tab.tooltip = categories[i].Name;
                tab.AddToClassList("emoji-category");
                _categoryBar.Add(tab);
            }

            SelectCategory(0);
        }

        private void SelectCategory(int index)
        {
            _selectedCategory = index;
            for (int i = 0; i < _categoryBar.childCount; i++)
            {
                _categoryBar[i].EnableInClassList("selected", i == index);
            }

            _grid.Clear();
            IReadOnlyList<EmojiEntry> entries = _database.Categories[index].Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                EmojiEntry entry = entries[i];
                Button button = new Button(() => _onEmojiChosen?.Invoke(entry.Character)) { text = entry.Character };
                button.tooltip = ":" + entry.Shortcode + ":";
                button.AddToClassList("emoji-button");
                _grid.Add(button);
            }
        }
    }
}
