using System;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.UIElements.Experimental;

namespace ClanSystem.Presentation
{
    /// <summary>
    /// The floating notification button, its unread badge and the panel it opens.
    ///
    /// The button is positioned from the window's own rect rather than from fixed coordinates, so
    /// it lands in the same visual corner at any resolution or aspect ratio, and is re-clamped
    /// whenever the window resizes. The badge is a child of the button, so it inherits every drag
    /// and every snap for free - there is no second position that could fall out of step.
    /// </summary>
    public class NotificationDockController : IDisposable
    {
        private const string _visibleClass = "visible";
        private const string _openClass = "open";
        private const string _draggingClass = "dragging";
        private const string _pressedClass = "pressed";

        /// <summary>Distance a pointer must travel before the gesture counts as a drag, not a tap.</summary>
        private const float _dragThreshold = 6f;

        /// <summary>Gap kept between the button and the window edge when it rests in a corner.</summary>
        private const float _edgeMargin = 18f;

        /// <summary>
        /// Reserved along the top edge so a snapped button never lands on the window chrome. Covers
        /// the header and the tab bar below it - stopping at the header alone puts the button on
        /// top of the first tab.
        /// </summary>
        private const float _topReserved = 176f;

        private const int _followIntervalMs = 16;

        /// <summary>
        /// How far the button closes the distance to the pointer each tick. Following the pointer
        /// exactly reads as a cursor-locked object; easing towards it gives the weight a floating
        /// button is expected to have while still keeping up with a fast drag.
        /// </summary>
        private const float _followFactor = 0.35f;

        private const int _snapDurationMs = 620;

        /// <summary>Gap between the button and the panel it opens.</summary>
        private const float _panelGap = 12f;

        private const float _panelMaxHeight = 520f;
        private const float _panelMinHeight = 200f;

        /// <summary>Used only before the panel has been laid out once; matches the stylesheet.</summary>
        private const float _panelFallbackWidth = 420f;

        /// <summary>How often the first placement re-checks for a laid-out window.</summary>
        private const int _placementIntervalMs = 32;

        private readonly VisualElement _root;
        private readonly VisualElement _dock;
        private readonly VisualElement _panel;
        private readonly Label _badge;
        private readonly NotificationInbox _inbox;
        private readonly NotificationsTabController _panelContent;

        private readonly IVisualElementScheduledItem _follow;
        private readonly IVisualElementScheduledItem _placement;

        private Vector2 _target;
        private Vector2 _pointerGrabOffset;
        private Vector2 _pressPosition;
        private ValueAnimation<Vector2> _snap;
        private int _activePointer = -1;
        private bool _isDragging;
        private bool _isPlaced;
        private bool _isPlayerPositioned;
        private bool _isOpen;
        private bool _isDisposed;

        public NotificationDockController(VisualElement root, NotificationInbox inbox, NotificationsTabController panelContent)
        {
            _root = root ?? throw new ArgumentNullException(nameof(root));
            _inbox = inbox;
            _panelContent = panelContent;

            _dock = root.Q<VisualElement>("notif-dock");
            _panel = root.Q<VisualElement>("notif-panel");
            _badge = root.Q<Label>("notif-badge");

            if (_dock == null)
            {
                return;
            }

            // The USS default is hidden, so the button does not sit unowned over the sign-in
            // screen; this controller only exists once a player is signed in, so revealing it here
            // is what ties "visible" to "there is a session for it to represent".
            _dock.style.display = DisplayStyle.Flex;

            _dock.RegisterCallback<PointerDownEvent>(PointerDownCallback);
            _dock.RegisterCallback<PointerMoveEvent>(PointerMoveCallback);
            _dock.RegisterCallback<PointerUpEvent>(PointerUpCallback);
            _dock.RegisterCallback<PointerCaptureOutEvent>(PointerCaptureOutCallback);

            // Placement needs two rects that only exist after layout: the window's and the
            // button's own. The button's geometry callback is what fires first with both known -
            // the root's can run while the button is still zero-sized - so first placement hangs
            // off the button and the root callback only re-clamps on later resizes.
            _dock.RegisterCallback<GeometryChangedEvent>(DockGeometryChangedCallback);
            _root.RegisterCallback<GeometryChangedEvent>(RootGeometryChangedCallback);

            Button close = root.Q<Button>("notif-close");
            if (close != null)
            {
                close.clicked += Close;
            }

            _follow = _dock.schedule.Execute(AdvanceFollow).Every(_followIntervalMs);
            _follow.Pause();

            // The dock element is part of the document from the start, so by the time this
            // controller is built - after sign-in - its layout has usually already settled and no
            // further geometry event is coming. Placement therefore also runs from a tick that
            // retires itself once the button has a corner, rather than relying on an event that
            // may already have fired.
            _placement = _dock.schedule.Execute(PlacementTick).Every(_placementIntervalMs);

            if (_inbox != null)
            {
                _inbox.Changed += InboxChangedCallback;
            }

            RefreshBadge();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _follow?.Pause();
            _placement?.Pause();
            StopSnap();
            _dock?.UnregisterCallback<GeometryChangedEvent>(DockGeometryChangedCallback);
            _root.UnregisterCallback<GeometryChangedEvent>(RootGeometryChangedCallback);

            if (_inbox != null)
            {
                _inbox.Changed -= InboxChangedCallback;
            }
        }

        public void Open()
        {
            if (_panel == null || _isOpen)
            {
                return;
            }

            _isOpen = true;

            // Place it before it becomes visible: the panel only gets repositioned when the button
            // moves, and opening it after a window resize is a case where the button has not.
            PositionPanel(CurrentPosition());
            _panel.AddToClassList(_openClass);

            // Opening reads the visible category, which is what clears its share of the badge.
            _panelContent?.Activate();
        }

        public void Close()
        {
            if (_panel == null || !_isOpen)
            {
                return;
            }

            _isOpen = false;
            _panel.RemoveFromClassList(_openClass);
        }

        public void Toggle()
        {
            if (_isOpen)
            {
                Close();
            }
            else
            {
                Open();
            }
        }

        /// <summary>
        /// Unread count, capped at the two characters the badge can hold. Zero hides the badge
        /// rather than showing a nought, which is the difference between "nothing waiting" and
        /// "something waiting that happens to be small".
        /// </summary>
        private void RefreshBadge()
        {
            if (_badge == null)
            {
                return;
            }

            int unread = _inbox != null ? _inbox.TotalUnread : 0;
            _badge.text = unread > 9 ? "9+" : unread.ToString();
            _badge.EnableInClassList(_visibleClass, unread > 0);
        }

        private void PointerDownCallback(PointerDownEvent evt)
        {
            if (_activePointer != -1)
            {
                return;
            }

            _activePointer = evt.pointerId;
            _isDragging = false;
            _pressPosition = (Vector2)evt.position;
            _pointerGrabOffset = _pressPosition - CurrentPosition();
            StopSnap();

            _dock.AddToClassList(_pressedClass);
            _dock.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void PointerMoveCallback(PointerMoveEvent evt)
        {
            if (evt.pointerId != _activePointer || !_dock.HasPointerCapture(evt.pointerId))
            {
                return;
            }

            Vector2 position = (Vector2)evt.position;
            if (!_isDragging)
            {
                // Below the threshold the gesture is still a tap. Without this a one-pixel jitter
                // during a click would both move the button and swallow the press.
                if ((position - _pressPosition).sqrMagnitude < _dragThreshold * _dragThreshold)
                {
                    return;
                }

                _isDragging = true;
                _dock.RemoveFromClassList(_pressedClass);
                _dock.AddToClassList(_draggingClass);
                StopSnap();
                _follow.Resume();
            }

            _target = ClampToWindow(position - _pointerGrabOffset);
            evt.StopPropagation();
        }

        private void PointerUpCallback(PointerUpEvent evt)
        {
            if (evt.pointerId != _activePointer)
            {
                return;
            }

            bool wasDragging = _isDragging;
            ReleasePointer(evt.pointerId);

            if (wasDragging)
            {
                _isPlayerPositioned = true;
                SnapToNearestCorner();
            }
            else
            {
                Toggle();
            }

            evt.StopPropagation();
        }

        private void PointerCaptureOutCallback(PointerCaptureOutEvent evt)
        {
            // Capture can be lost without a matching up event - the window losing focus mid-drag,
            // for one. Settle the button rather than leaving it following a pointer that is gone.
            if (evt.pointerId != _activePointer)
            {
                return;
            }

            bool wasDragging = _isDragging;
            ReleasePointer(evt.pointerId);

            if (wasDragging)
            {
                _isPlayerPositioned = true;
                SnapToNearestCorner();
            }
        }

        private void ReleasePointer(int pointerId)
        {
            if (_dock.HasPointerCapture(pointerId))
            {
                _dock.ReleasePointer(pointerId);
            }

            _activePointer = -1;
            _isDragging = false;
            _follow.Pause();
            _dock.RemoveFromClassList(_draggingClass);
            _dock.RemoveFromClassList(_pressedClass);
        }

        /// <summary>
        /// Eases the button towards the pointer while a drag is running. Running on the scheduler
        /// rather than inside the move event means the motion keeps closing the gap even when the
        /// pointer stops, so the button arrives instead of freezing short.
        /// </summary>
        private void AdvanceFollow()
        {
            if (!_isDragging)
            {
                return;
            }

            Vector2 position = Vector2.Lerp(CurrentPosition(), _target, _followFactor);
            ApplyPosition(position);
        }

        /// <summary>
        /// Sends the button to whichever corner it was released nearest, on an elastic curve. The
        /// corners are computed from the current window rect, so the same gesture lands correctly
        /// at any resolution, and the top corners sit below the header rather than over it.
        /// </summary>
        private void SnapToNearestCorner()
        {
            Rect bounds = AllowedBounds();
            if (bounds.width <= 0f || bounds.height <= 0f)
            {
                return;
            }

            Vector2 current = CurrentPosition();
            float x = current.x + _dock.resolvedStyle.width * 0.5f < bounds.center.x ? bounds.xMin : bounds.xMax;
            float y = current.y + _dock.resolvedStyle.height * 0.5f < bounds.center.y ? bounds.yMin : bounds.yMax;

            AnimateTo(new Vector2(x, y));
        }

        private void AnimateTo(Vector2 destination)
        {
            Vector2 from = CurrentPosition();
            if ((from - destination).sqrMagnitude < 0.01f)
            {
                ApplyPosition(destination);
                return;
            }

            StopSnap();
            // KeepAlive, because an animation that finishes on its own is recycled by the panel and
            // stopping a recycled one throws. Holding it alive lets a new gesture cancel the tail
            // of the previous snap instead of tripping over its corpse; it is released in StopSnap.
            _snap = _dock.experimental.animation
                .Start(from, destination, _snapDurationMs, (element, value) => ApplyPosition(value))
                .Ease(Easing.OutElastic)
                .KeepAlive();
        }

        /// <summary>Ends a snap in flight, so a new gesture is never fought by the previous one.</summary>
        private void StopSnap()
        {
            if (_snap == null)
            {
                return;
            }

            _snap.Stop();
            _snap.Recycle();
            _snap = null;
        }

        private Vector2 CurrentPosition()
        {
            if (!_isPlaced)
            {
                return new Vector2(_dock.resolvedStyle.left, _dock.resolvedStyle.top);
            }

            return new Vector2(_dock.style.left.value.value, _dock.style.top.value.value);
        }

        private void ApplyPosition(Vector2 position)
        {
            Vector2 clamped = ClampToWindow(position);
            _isPlaced = true;
            _dock.style.left = clamped.x;
            _dock.style.top = clamped.y;
            PositionPanel(clamped);
        }

        /// <summary>
        /// Places the panel against the button, on whichever side has room, and clamped to the
        /// window. Called from the same method that moves the button - including every frame of a
        /// drag and every step of a snap - so the two travel as one object rather than the panel
        /// catching up afterwards.
        ///
        /// The panel opens towards the middle of the window: a button in the bottom-right corner
        /// gets a panel above it and to the left, which is the only direction with space.
        /// </summary>
        private void PositionPanel(Vector2 dockPosition)
        {
            if (_panel == null)
            {
                return;
            }

            Rect window = _dock.parent != null ? _dock.parent.contentRect : _root.contentRect;
            float dockWidth = _dock.resolvedStyle.width;
            float dockHeight = _dock.resolvedStyle.height;
            if (window.width <= 0f || dockWidth <= 0f)
            {
                return;
            }

            // Height is driven here because the panel no longer spans between two anchors: it is
            // as tall as the window allows, up to its own maximum.
            float available = Mathf.Max(_panelMinHeight, window.height - _topReserved - _edgeMargin);
            float height = Mathf.Min(_panelMaxHeight, available);
            _panel.style.height = height;

            float width = _panel.resolvedStyle.width > 0f ? _panel.resolvedStyle.width : _panelFallbackWidth;
            bool isRightHalf = dockPosition.x + dockWidth * 0.5f > window.center.x;
            bool isBottomHalf = dockPosition.y + dockHeight * 0.5f > window.center.y;

            float left = isRightHalf
                ? dockPosition.x + dockWidth - width
                : dockPosition.x;
            float top = isBottomHalf
                ? dockPosition.y - _panelGap - height
                : dockPosition.y + dockHeight + _panelGap;

            left = Mathf.Clamp(left, _edgeMargin, Mathf.Max(_edgeMargin, window.width - width - _edgeMargin));
            top = Mathf.Clamp(top, _topReserved, Mathf.Max(_topReserved, window.height - height - _edgeMargin));

            _panel.style.left = left;
            _panel.style.top = top;

            // Grow from the corner nearest the button, so the open animation reads as the panel
            // coming out of the button rather than expanding from an unrelated point.
            _panel.style.transformOrigin = new StyleTransformOrigin(new TransformOrigin(
                Length.Percent(isRightHalf ? 100f : 0f),
                Length.Percent(isBottomHalf ? 100f : 0f)));
        }

        /// <summary>
        /// The rectangle the button's top-left corner may occupy: the window inset by the edge
        /// margin and the button's own size, with the header strip excluded along the top.
        /// </summary>
        private Rect AllowedBounds()
        {
            Rect window = _dock.parent != null ? _dock.parent.contentRect : _root.contentRect;
            float width = _dock.resolvedStyle.width;
            float height = _dock.resolvedStyle.height;
            if (window.width <= 0f || width <= 0f)
            {
                return Rect.zero;
            }

            float left = _edgeMargin;
            float top = _topReserved;
            float right = Mathf.Max(left, window.width - width - _edgeMargin);
            float bottom = Mathf.Max(top, window.height - height - _edgeMargin);
            return Rect.MinMaxRect(left, top, right, bottom);
        }

        private Vector2 ClampToWindow(Vector2 position)
        {
            Rect bounds = AllowedBounds();
            if (bounds.width < 0f)
            {
                return position;
            }

            return new Vector2(
                Mathf.Clamp(position.x, bounds.xMin, bounds.xMax),
                Mathf.Clamp(position.y, bounds.yMin, bounds.yMax));
        }

        private void PlacementTick()
        {
            if (EnsurePlaced() || _isPlayerPositioned)
            {
                _placement.Pause();
            }
        }

        private void DockGeometryChangedCallback(GeometryChangedEvent evt)
        {
            EnsurePlaced();
        }

        private void RootGeometryChangedCallback(GeometryChangedEvent evt)
        {
            if (EnsurePlaced() || _isDragging)
            {
                return;
            }

            // A resize can leave the button outside the new window; pull it back in.
            ApplyPosition(CurrentPosition());
        }

        /// <summary>
        /// Parks the button in its default corner until the player has moved it themselves.
        /// Returns true on the passes that placed it.
        ///
        /// This re-runs on every layout rather than only the first, because early passes can report
        /// a window that is not laid out yet - a full-height rect with no width, say - and parking
        /// against that would strand the button in the wrong corner for the rest of the session.
        /// A completed drag ends the re-parking: from then on the position is the player's.
        /// </summary>
        private bool EnsurePlaced()
        {
            if (_isPlayerPositioned || _dock == null || _dock.resolvedStyle.width <= 0f)
            {
                return false;
            }

            Rect bounds = AllowedBounds();
            if (bounds.width <= 0f || bounds.height <= 0f)
            {
                return false;
            }

            ApplyPosition(new Vector2(bounds.xMax, bounds.yMax));
            return true;
        }

        private void InboxChangedCallback()
        {
            RefreshBadge();
        }
    }
}
