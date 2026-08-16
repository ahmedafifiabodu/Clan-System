using System;
using ClanSystem.CoreData;
using UnityEngine.UIElements;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// Builds the small repeated row layouts used by every list in the social window.
    /// Rows are plain <see cref="VisualElement"/> trees so no prefab wiring can go stale.
    /// </summary>
    public static class SocialRowFactory
    {
        public static VisualElement CreateRow(string title, string subtitle, bool isSelf)
        {
            VisualElement row = new VisualElement();
            row.AddToClassList("row");
            if (isSelf)
            {
                row.AddToClassList("self");
            }

            VisualElement main = new VisualElement();
            main.AddToClassList("row-main");

            Label titleLabel = new Label(title);
            titleLabel.AddToClassList("row-title");
            main.Add(titleLabel);

            if (!string.IsNullOrEmpty(subtitle))
            {
                Label subtitleLabel = new Label(subtitle);
                subtitleLabel.AddToClassList("row-sub");
                main.Add(subtitleLabel);
            }

            row.Add(main);

            VisualElement actions = new VisualElement();
            actions.name = "row-actions";
            actions.AddToClassList("row-actions");
            row.Add(actions);

            return row;
        }

        public static VisualElement CreatePresenceRow(string title, string subtitle, bool isOnline)
        {
            VisualElement row = CreateRow(title, subtitle, false);
            VisualElement dot = new VisualElement();
            dot.AddToClassList("presence-dot");
            if (isOnline)
            {
                dot.AddToClassList("online");
            }

            row.Insert(0, dot);
            return row;
        }

        public static void AddRoleBadge(VisualElement row, ClanRole role)
        {
            if (role == ClanRole.None)
            {
                return;
            }

            Label badge = new Label(role.ToString().ToUpperInvariant());
            badge.AddToClassList("badge");
            if (role == ClanRole.Owner)
            {
                badge.AddToClassList("owner");
            }
            else if (role == ClanRole.Officer)
            {
                badge.AddToClassList("officer");
            }

            VisualElement main = row.Q<VisualElement>(className: "row-main");
            VisualElement titleRow = new VisualElement();
            titleRow.style.flexDirection = FlexDirection.Row;
            titleRow.style.alignItems = Align.Center;

            if (main != null && main.childCount > 0)
            {
                VisualElement title = main[0];
                main.Remove(title);
                titleRow.Add(title);
                titleRow.Add(badge);
                main.Insert(0, titleRow);
            }
        }

        public static Button AddAction(VisualElement row, string text, Action callback, bool isDanger = false)
        {
            Button button = new Button(callback) { text = text };
            button.AddToClassList("button");
            button.AddToClassList("mini");
            if (isDanger)
            {
                button.AddToClassList("danger");
            }

            VisualElement actions = row.Q<VisualElement>("row-actions");
            if (actions != null)
            {
                actions.Add(button);
            }
            else
            {
                row.Add(button);
            }

            return button;
        }

        public static VisualElement CreateChatLine(CommMessage message)
        {
            VisualElement line = new VisualElement();
            line.AddToClassList("chat-line");
            if (message.IsFromSelf)
            {
                line.AddToClassList("mine");
            }

            Label meta = new Label($"{message.SenderName ?? "Player"}  -  {message.TimestampLocal:HH:mm}");
            meta.AddToClassList("chat-meta");
            line.Add(meta);

            Label text = new Label(message.Text);
            text.AddToClassList("chat-text");
            line.Add(text);

            return line;
        }

        public static VisualElement CreateLeaderboardRow(LeaderboardRow row, bool isClanBoard)
        {
            VisualElement element = new VisualElement();
            element.AddToClassList("row");
            if (row.IsSelf)
            {
                element.AddToClassList("self");
            }

            Label rank = new Label("#" + row.Rank);
            rank.AddToClassList("lb-cell");
            rank.AddToClassList("lb-rank");
            element.Add(rank);

            string displayName = row.Name;
            if (string.IsNullOrEmpty(displayName))
            {
                displayName = isClanBoard ? "Unknown clan" : "Unknown player";
            }

            if (isClanBoard && !string.IsNullOrEmpty(row.Tag))
            {
                displayName = $"[{row.Tag}] {displayName}";
            }
            else if (!isClanBoard && !string.IsNullOrEmpty(row.ClanTag))
            {
                displayName = $"{displayName}  [{row.ClanTag}]";
            }

            Label name = new Label(displayName);
            name.AddToClassList("lb-cell");
            name.AddToClassList("lb-name");
            element.Add(name);

            if (isClanBoard)
            {
                Label members = new Label(row.MemberCount + (row.MaxMembers > 0 ? "/" + row.MaxMembers : string.Empty));
                members.AddToClassList("lb-cell");
                members.AddToClassList("lb-value");
                element.Add(members);
            }

            Label score = new Label(row.Score.ToString("N0"));
            score.AddToClassList("lb-cell");
            score.AddToClassList("lb-value");
            element.Add(score);

            return element;
        }

        public static void FillLeaderboardHeader(VisualElement header, bool isClanBoard)
        {
            header.Clear();
            header.Add(MakeHeaderCell("RANK", "lb-rank"));
            header.Add(MakeHeaderCell(isClanBoard ? "CLAN" : "PLAYER", "lb-name"));
            if (isClanBoard)
            {
                header.Add(MakeHeaderCell("MEMBERS", "lb-value"));
            }

            header.Add(MakeHeaderCell("SCORE", "lb-value"));
        }

        private static Label MakeHeaderCell(string text, string widthClass)
        {
            Label label = new Label(text);
            label.AddToClassList("lb-cell");
            label.AddToClassList(widthClass);
            return label;
        }
    }
}
