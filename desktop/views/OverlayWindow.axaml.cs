using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;
using ColorEnum = LiveCaptionsTranslator.utils.Color;

namespace LiveCaptionsTranslator
{
    // The floating caption window: always on top, no frame, resized from its edges.
    //
    // Its height follows the text. The translation wraps to two or three lines and back all the time, and at a
    // fixed height the line below it would be cut off. The window is never lower than the user made it
    // (`restHeight`), and higher only for as long as the text needs more; where it would then reach below the
    // screen it moves up for that long (`userTop` keeps where it was). What is remembered is the window as the
    // user left it, without what the text added. (Not with SizeToContent: on X11 that switches itself off with
    // the first resize of a window that can be resized.)
    public partial class OverlayWindow : Window
    {
        private static readonly Dictionary<ColorEnum, Avalonia.Media.Color> colorMap = new()
        {
            { ColorEnum.White, Colors.White },
            { ColorEnum.Yellow, Colors.Yellow },
            { ColorEnum.LimeGreen, Colors.LimeGreen },
            { ColorEnum.Aqua, Colors.Aqua },
            { ColorEnum.Blue, Colors.Blue },
            { ColorEnum.DeepPink, Colors.DeepPink },
            { ColorEnum.Red, Colors.Red },
            { ColorEnum.Black, Colors.Black },
        };
        private CaptionVisible onlyMode = CaptionVisible.Both;

        public CaptionVisible OnlyMode
        {
            get => onlyMode;
            set
            {
                // A height the user has just given the window belongs to the lines shown until now.
                if (userResize.IsEnabled)
                    EndUserResize();
                onlyMode = value;
                ResizeForOnlyMode();
            }
        }
        public CaptionLocation SwitchMode { get; set; } = CaptionLocation.TranslationTop;

        private static OverlayWindowState Look => Translator.Setting.OverlayWindow;

        // The height with both lines shown while the text needs no more: the one the window was opened with, or
        // the one the user last gave it. This is the height that is remembered.
        private double restHeight;
        // Runs while the user resizes the window, until the size has stopped changing for a moment. (The window
        // manager does the resizing: when it ends is not told.)
        private readonly DispatcherTimer userResize = new() { Interval = TimeSpan.FromMilliseconds(SETTLE_MS) };
        private double heightBeforeResize;
        // The heights the window had or was given by the code here, the latest last, and when a height was last
        // asked for: the window reports it back a moment later. A window manager may also give the window one
        // of the earlier ones back (openbox: the height it had when the user began to move it).
        private readonly List<double> ownHeights = new();
        private const int MAX_OWN_HEIGHTS = 16;
        private long requestedTick;
        // The height the window was last to have: the one the texts needed (asked for, or the one it had
        // already), or the one the user has given it since.
        private double wantedHeight;
        private Size lastSize;
        private const int SETTLE_MS = 500;
        // The top the user gave the window, while the code holds it higher than that (at `raisedTop`), so that
        // text which needs more than `restHeight` stays on the screen. Null: the window is where they put it.
        private int? userTop;
        private int raisedTop;
        // Where the window is as far as known here: where it was last asked to be, or last seen to be.
        // `Position` is behind on X11 (a move arrives a moment later), and where the window manager does not
        // move windows for programs the move never arrives.
        private PixelPoint? knownPosition;
        // The places asked for and not seen yet, oldest first: on X11 a move is reported a moment later, and
        // the next one may have been asked for by then.
        private readonly List<PixelPoint> asked = new();
        private const int MAX_ASKED = 8;
        // Runs while the user moves the window, until it has not moved for a moment. (The window manager does
        // the moving: when it ends is not told.) For that long the window is left alone: a window manager may
        // not move a window for a program while the user holds it, or may undo what the program did to it.
        // See BeingMoved.
        private readonly DispatcherTimer userMove = new() { Interval = TimeSpan.FromMilliseconds(SETTLE_MS) };
        // Runs from when the text asks for another height, or the window reports a height the text does not
        // need now, until neither has happened for a moment: then the height and the place are looked at once
        // more. A height is not always given as asked. One that is replaced before the next layout pass never
        // reaches the window manager, and one asked for while the answer to another is on its way is dropped
        // (Avalonia compares it with the size last reported). Then no size is reported, or only the one asked
        // for before, and the window would stay as it is until the next caption.
        private readonly DispatcherTimer refit = new() { Interval = TimeSpan.FromMilliseconds(SETTLE_MS) };
        // 1 while a ShowCaption is on its way to the UI thread: the texts change one after another, and one
        // drawing shows them all.
        private int captionPosted;
        // How far the tip of a tool is from its button (see PlaceTip).
        private const double TIP_GAP = 6;

        public OverlayWindow()
        {
            InitializeComponent();

            WindowBounds.Restore(this, "OverlayWindow", new Size(480, 100));
            // A width of exactly 650 counts as unset and gets the default width.
            if (Math.Abs(Width - 650) < 1)
                Width = 480;
            restHeight = Height;
            wantedHeight = Height;
            ownHeights.Add(Height);
            lastSize = new Size(Width, Height);
            WindowBounds.Track(this, "OverlayWindow", WithBothLines);

            Opened += (s, e) => Translator.Caption.PropertyChanged += OnCaptionChanged;
            Closed += (s, e) => Translator.Caption.PropertyChanged -= OnCaptionChanged;
            Opened += (s, e) => FitHeight();
            Closed += (s, e) => userResize.Stop();
            Closed += (s, e) => userMove.Stop();
            Closed += (s, e) => refit.Stop();
            userResize.Tick += (s, e) => EndUserResize();
            userMove.Tick += (s, e) => EndUserMove();
            refit.Tick += (s, e) =>
            {
                refit.Stop();
                FitHeight(again: false);
            };
            Resized += (s, e) => OnResized();
            PositionChanged += (s, e) => OnMoved(e.Point);

            FloatLock.Changed += ApplyLock;
            Closed += (s, e) => FloatLock.Changed -= ApplyLock;
            Opened += (s, e) => ApplyLock();

            PointerEntered += (s, e) => ControlPanel.IsVisible = !FloatLock.Locked;
            PointerExited += (s, e) => ControlPanel.IsVisible = false;
            foreach (var tool in ControlPanel.Children.OfType<Button>())
            {
                ToolTip.SetPlacement(tool, PlacementMode.Custom);
                ToolTip.SetCustomPopupPlacementCallback(tool, PlaceTip);
            }
            // On the card as a whole: the grid in it only takes the pointer where there is text, and before the
            // first sentence there is none.
            BorderBackground.PointerPressed += (s, e) =>
            {
                if (!FloatLock.Locked && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.Source is not Button)
                {
                    BeginUserMove();
                    BeginMoveDrag(e);
                }
            };
            Resizer(TopEdge, WindowEdge.North);
            Resizer(BottomEdge, WindowEdge.South);
            Resizer(LeftEdge, WindowEdge.West);
            Resizer(RightEdge, WindowEdge.East);
            Resizer(TopLeftEdge, WindowEdge.NorthWest);
            Resizer(TopRightEdge, WindowEdge.NorthEast);
            Resizer(BottomLeftEdge, WindowEdge.SouthWest);
            Resizer(BottomRightEdge, WindowEdge.SouthEast);

            ApplyBackgroundOpacity();
            ShowCaption();
        }

        // The tip of a tool is shown above its button, or below it where there is no room above, a little away
        // from it either way: not where the pointer is when the tip comes up, nor where it is next when it
        // leaves the button. A tip is a window of its own: under the pointer it takes the pointer from this
        // window, and the tools go away with it (PointerExited), until the pointer moves again. At the pointer,
        // where tips are by default, one that has no room below it (at the bottom of the screen, where captions
        // are) is put over the pointer; and right next to the button, the pointer is on the tip as soon as it
        // leaves the button on that side. (A pointer fast enough to go from the button to the tip in one step
        // still gets onto it: the tools are back with its next step.)
        private static void PlaceTip(CustomPopupPlacement placement)
        {
            // (Not with an offset: that is not turned round with the tip when it goes below the button.)
            placement.AnchorRectangle = placement.AnchorRectangle.Inflate(new Thickness(0, TIP_GAP));
            placement.Anchor = PopupAnchor.Top;
            placement.Gravity = PopupGravity.Top;
            placement.Offset = default;
        }

        private void Resizer(Border edge, WindowEdge direction)
        {
            edge.PointerPressed += (s, e) =>
            {
                if (FloatLock.Locked || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                    return;
                BeginUserResize(ClientSize.Height);
                BeginResizeDrag(direction, e);
            };
        }

        // While the user resizes the window its height is theirs to set: the text does not change it.
        private void BeginUserResize(double heightBefore)
        {
            if (!userResize.IsEnabled)
                heightBeforeResize = heightBefore;
            userResize.Stop();
            userResize.Start();
        }

        private void EndUserResize()
        {
            userResize.Stop();
            // A new width leaves the height as the text made it: that is not a height the user chose.
            if (Math.Abs(ClientSize.Height - heightBeforeResize) > 1)
            {
                // The window is now as high as the user wants it, where they want it.
                restHeight = ClientSize.Height + HiddenLineHeight;
                userTop = null;
            }
            // What the text needed before this is not a height the window may still get (see FitHeight).
            wantedHeight = ClientSize.Height;
            // At another width the text wraps differently.
            FitHeight();
            // Remembered now: the save that follows every change of size came before this, with the height as
            // it was, and nothing else may change for a long time.
            WindowBounds.Save(this, "OverlayWindow", WithBothLines);
        }

        // While the user moves the window it stays as it is: neither its height nor its place follows the text.
        // (Held still for longer than `SETTLE_MS` in the middle of a drag, it is fitted to the text where it is
        // held: when a drag ends is not told.)
        private void BeginUserMove()
        {
            userMove.Stop();
            userMove.Start();
        }

        private void EndUserMove()
        {
            userMove.Stop();
            // What the text needed in the meantime, and the window may have been let go below the screen. (Or
            // it is still on its way, see BeingMoved: then this is back here in a moment.)
            FitHeight();
        }

        // Whether the user is moving the window at this moment.
        private bool BeingMoved()
        {
            if (userMove.IsEnabled)
                return true;
            // Not every window manager tells where the window is while the user drags it (openbox tells when
            // they let go of it), so on X11 the server is asked. Where the window is expected: where it is known
            // to be, or, with a move asked for and not seen yet, where it was or at a place asked for before.
            // Anywhere else, someone is moving it. Going by a place it has left would bring it back there.
            if (!IsVisible || XWindowPlace.Of(this) is not PixelPoint actual)
                return false;
            bool expected = Near(actual, CurrentPosition) ||
                            (asked.Count > 0 && (Near(actual, Position) || asked.Exists(place => Near(place, actual))));
            if (expected)
                return false;
            OnMoved(actual);
            return userMove.IsEnabled;
        }

        private void OnResized()
        {
            var size = ClientSize;
            var before = lastSize;
            if (Math.Abs(size.Width - before.Width) <= 0.5 && Math.Abs(size.Height - before.Height) <= 0.5)
                return;
            lastSize = size;
            if (userResize.IsEnabled)
            {
                BeginUserResize(before.Height);     // Still resizing.
                return;
            }
            // A height given to the window here, or, right after asking for one, what the window manager made
            // of it.
            bool own = Math.Abs(size.Width - before.Width) <= 0.5 &&
                       (ownHeights.Exists(height => Math.Abs(size.Height - height) <= 1) ||
                        Environment.TickCount64 - requestedTick <= SETTLE_MS);
            // Any other size is the user's: one of the edges here is not the only way to resize a window (the
            // window manager has its own keys for it).
            if (!own)
            {
                BeginUserResize(before.Height);
                return;
            }
            // Not the height the text needs now: one asked for before it (the one needed now was not sent
            // while that one was on its way, see `refit`), or what the window manager made of it.
            if (!SameOnScreen(size.Height, wantedHeight))
                FitAgainSoon();
            // Now that the window is lower it can go back down (see FitHeight).
            if (!BeingMoved())
                KeepBottomOnScreen(wantedHeight);
        }

        // Whether a window that is to be `wanted` high is that high when its height is `actual`. A window is
        // whole pixels high: at a scaling of 1.25, one asked to be 103 high is 128 pixels high and reports 102.4.
        private bool SameOnScreen(double actual, double wanted) => Math.Abs(actual - wanted) * RenderScaling < 0.999;

        // While only one of the two lines is shown, the window is this much lower (see ResizeForOnlyMode).
        private double HiddenLineHeight => onlyMode == CaptionVisible.Both ? 0 : StyleConsts.DELTA_OVERLAY_HEIGHT;

        // Makes the window as high as its two texts need at its width, and no lower than the user made it.
        // `again`: whether to look once more when the text has stood still for a moment (see `refit`).
        private void FitHeight(bool again = true)
        {
            if (userResize.IsEnabled || BeingMoved())
                return;
            double width = ClientSize.Width > 0 ? ClientSize.Width : Width;
            if (double.IsNaN(width) || width <= 0)
                return;

            // The margin round the card as the layout makes it, in whole pixels: at a scaling of 1.5 a margin of
            // 5 is 5.33 wide. Measured at a width a little greater than the one it is given, a text may need a
            // line less than it does in the window, and its last line would be cut off.
            double scale = LayoutHelper.GetLayoutScale(BorderBackground);
            var margin = LayoutHelper.RoundLayoutThickness(BorderBackground.Margin, scale, scale);
            var available = new Size(Math.Max(width - margin.Left - margin.Right, 0), double.PositiveInfinity);
            double needed = margin.Top + margin.Bottom;
            foreach (var card in new[] { TranslatedCaptionCard, OriginalCaptionCard })
            {
                // Measured anew. A card whose text has just changed still counts as measured (only the text in
                // it does not, until the next layout pass), and asked for the same size as the last time it
                // answers with what the text before needed: the window would be one caption behind, and stay
                // so when no caption follows.
                card.InvalidateMeasure();
                // The tools shown under the pointer are not counted: they lie over the text.
                card.Measure(available);
                needed += card.DesiredSize.Height;
            }
            double height = Math.Max(Math.Max(Math.Ceiling(needed), restHeight - HiddenLineHeight), MinHeight);
            ownHeights.RemoveAll(own => Math.Abs(own - height) <= 0.5);
            if (ownHeights.Count >= MAX_OWN_HEIGHTS)
                ownHeights.RemoveAt(0);
            ownHeights.Add(height);
            // Up before it grows, and down only once it has shrunk (see OnResized): moved down at the height it
            // still has, it would reach below the screen for a moment, and a window manager that keeps windows
            // on the screen (GNOME's, KDE's) would not let it go there at all. So the place is the one for the
            // highest the window may be at this moment: what it was to be before, and what it is to be now.
            // (It may never get the height it was to have before, and then no size is reported that would
            // bring it down: see `refit`.)
            double before = wantedHeight;
            wantedHeight = height;
            KeepBottomOnScreen(Math.Max(height, before));
            if (!SameOnScreen(Height, height))
            {
                requestedTick = Environment.TickCount64;
                Height = height;
            }
            if (again && Math.Abs(before - height) > 0.5)
                FitAgainSoon();
        }

        // (A call of FitHeight in the meantime does not make up for this one: it may come before the size
        // that was on its way is reported.)
        private void FitAgainSoon()
        {
            refit.Stop();
            refit.Start();
        }

        // Where the window is (see `knownPosition`).
        private PixelPoint CurrentPosition => knownPosition ?? Position;

        private void MoveTo(PixelPoint position)
        {
            // (Where the window manager moves no windows for programs, none of these places is ever seen.)
            if (asked.Count >= MAX_ASKED)
                asked.RemoveAt(0);
            asked.Add(position);
            knownPosition = position;
            Position = position;
        }

        // A platform may round the position it is given, or the one it reports.
        private static bool Near(PixelPoint a, PixelPoint b) => Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2;

        // The window is reported at a place. Either that is a place asked for here, or the window is there
        // already, or it was moved by someone else. Taking one of the first two for a move by the user would
        // leave the window up where the text had moved it, and remember that place: a little higher every time.
        // What is told apart is the place alone, not how long ago a move was asked for: while the user holds
        // the window the window manager may not make that move at all, and what the user does next would be
        // taken for its answer.
        private void OnMoved(PixelPoint position)
        {
            // Where it was asked to be, however late that is reported. The places asked for before that one are
            // done with as well (not every one of them is reported); the ones after it are still to come.
            int seen = asked.FindLastIndex(place => Near(place, position));
            if (seen >= 0)
            {
                if (seen == asked.Count - 1)
                    knownPosition = asked[seen];
                asked.RemoveRange(0, seen + 1);
                return;
            }
            // Where it is already: macOS reports the place again with every change of the height. (A move of
            // a pixel or two is not lost: it counts once the window is further away than that.)
            if (knownPosition is PixelPoint known && Near(known, position))
                return;

            // Moved by someone else: the user, dragging it here or with the keys of the window manager, or the
            // window manager itself, which put it elsewhere than asked. Moved up or down, the window is where it
            // is wanted now, whatever the text had made of its place; moved sideways only, it still goes back
            // down when the text is shorter.
            knownPosition = position;
            asked.Clear();
            if (userTop != null && Math.Abs(position.Y - raisedTop) > 2)
                userTop = null;
            BeginUserMove();
        }

        // The window grows downwards. At the bottom of the screen, where captions usually are, the new line
        // would be off the screen: there the window is moved up by as much as it would stick out, and back down
        // when the text is shorter again. `height` is the height the window is to have; while it is still
        // higher than that, the place is the one for the height it has.
        private void KeepBottomOnScreen(double height)
        {
            try
            {
                if (!IsVisible)
                    return;
                var position = CurrentPosition;
                // Where the user put it. (Positions and the screen are in units of `DesktopScaling`, which on
                // macOS is not the scaling the window is drawn with.)
                int top = userTop ?? position.Y;
                int shift = 0;
                if (Screens.ScreenFromVisual(this)?.WorkingArea is PixelRect area)
                {
                    height = Math.Max(height, ClientSize.Height);
                    int over = top + (int)Math.Ceiling(height * DesktopScaling) - area.Bottom;
                    shift = Math.Clamp(over, 0, Math.Max(top - area.Y, 0));
                }
                var target = new PixelPoint(position.X, top - shift);
                userTop = shift > 0 ? top : null;
                raisedTop = target.Y;
                // From the first time it is asked for, the window counts as being there (`knownPosition`).
                if (target.Y != position.Y)
                    MoveTo(target);
                // Not seen there yet: asked for again. (Held still by the user for a while, the window was
                // perhaps not moved for anyone else, and it is not told when they let go of it.)
                else if (asked.Count > 0 && asked[^1] == target)
                    Position = target;
            }
            catch (Exception)
            {
                // Where the window is, is cosmetic: it grows where it is.
            }
        }

        // The window opens with both lines, at the height the user gave it, so that is how its place is
        // remembered: while only one line is shown it sits lower (see ResizeForOnlyMode), and while its text is
        // long it is higher, and may have moved up for it.
        private Rect WithBothLines(Rect bounds)
        {
            double y = userTop ?? CurrentPosition.Y;
            if (onlyMode == CaptionVisible.TranslationOnly)
                y -= (int)(StyleConsts.DELTA_OVERLAY_HEIGHT * DesktopScaling);
            return new Rect(bounds.X, y, bounds.Width, restHeight);
        }

        // A colour number in the settings file that this version does not have (a file edited by hand, or written
        // by another version) must not keep the window from opening.
        private static Avalonia.Media.Color FontColor =>
            colorMap.TryGetValue(Look.FontColor, out var color) ? color : Colors.White;
        private static Avalonia.Media.Color BackgroundColor =>
            colorMap.TryGetValue(Look.BackgroundColor, out var color) ? color : Colors.Black;

        private void ApplyLock()
        {
            ClickThrough.Set(this, FloatLock.Locked);
            if (FloatLock.Locked)
                ControlPanel.IsVisible = false;
        }

        private void LockButton_Click(object? sender, RoutedEventArgs e)
        {
            FloatLock.Locked = true;
        }

        // The captions change on the translation threads: draw them on the UI thread. Only the overlay's own
        // texts matter here; the main window's caption changes many times a second as well.
        private void OnCaptionChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is not ("OverlayOriginalCaption" or "OverlayNoticePrefix" or "OverlayCurrentTranslation"
                or "OverlayPreviousTranslation"))
                return;
            if (Interlocked.Exchange(ref captionPosted, 1) == 1)
                return;
            Dispatcher.UIThread.Post(() =>
            {
                // Before the texts are read: a change after this is drawn by a call of its own. (With a full
                // barrier, like the exchange above: the texts read below are at least as new as this.)
                Interlocked.Exchange(ref captionPosted, 0);
                ShowCaption();
            });
        }

        private void ShowCaption()
        {
            var caption = Translator.Caption;
            var fontBrush = new SolidColorBrush(FontColor);
            OriginalCaption.FontSize = Look.FontSize;
            TranslatedCaption.FontSize = (int)(Look.FontSize * 1.25);
            ApplyFontBold();
            OriginalCaption.StrokeThickness = TranslatedCaption.StrokeThickness = Look.FontStroke;

            OriginalCaption.SetText(new (string, IBrush)[] { (caption.OverlayOriginalCaption, fontBrush) });
            TranslatedCaption.SetText(new (string, IBrush)[]
            {
                (caption.OverlayNoticePrefix, fontBrush),
                (caption.OverlayPreviousTranslation, fontBrush),
                (caption.OverlayCurrentTranslation, new SolidColorBrush(TranslationColor(fontBrush.Color))),
            });
            FitHeight();
        }

        // The current translation is a little off the text color, so it stands out from the previous ones.
        private static Avalonia.Media.Color TranslationColor(Avalonia.Media.Color color)
        {
            double target = 0.299 * color.R + 0.587 * color.G + 0.114 * color.B > 127 ? 0 : 255;
            byte r = (byte)Math.Clamp(color.R + (target - color.R) * 0.3, 0, 255);
            byte g = (byte)Math.Clamp(color.G + (target - color.G) * 0.4, 0, 255);
            byte b = (byte)Math.Clamp(color.B + (target - color.B) * 0.3, 0, 255);
            return Avalonia.Media.Color.FromRgb(r, g, b);
        }

        private void FontIncrease_Click(object? sender, RoutedEventArgs e)
        {
            if (Look.FontSize + StyleConsts.DELTA_FONT_SIZE < StyleConsts.MAX_FONT_SIZE)
            {
                Look.FontSize += StyleConsts.DELTA_FONT_SIZE;
                ShowCaption();
            }
        }

        private void FontDecrease_Click(object? sender, RoutedEventArgs e)
        {
            if (Look.FontSize - StyleConsts.DELTA_FONT_SIZE > StyleConsts.MIN_FONT_SIZE)
            {
                Look.FontSize -= StyleConsts.DELTA_FONT_SIZE;
                ShowCaption();
            }
        }

        private void FontBold_Click(object? sender, RoutedEventArgs e)
        {
            Look.FontBold++;
            if (Look.FontBold > utils.FontBold.Both)
                Look.FontBold = utils.FontBold.None;
            ApplyFontBold();
            // Bold text breaks its lines elsewhere.
            FitHeight();
        }

        private void ApplyFontBold()
        {
            bool original = Look.FontBold is utils.FontBold.SubtitleOnly or utils.FontBold.Both;
            bool translation = Look.FontBold is utils.FontBold.TranslationOnly or utils.FontBold.Both;
            OriginalCaption.FontWeight = original ? FontWeight.Bold : FontWeight.Normal;
            TranslatedCaption.FontWeight = translation ? FontWeight.Bold : FontWeight.Normal;
        }

        private void FontStrokeIncrease_Click(object? sender, RoutedEventArgs e)
        {
            if (Look.FontStroke + StyleConsts.DELTA_STROKE > StyleConsts.MAX_STROKE)
                return;
            Look.FontStroke += StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontStrokeDecrease_Click(object? sender, RoutedEventArgs e)
        {
            if (Look.FontStroke - StyleConsts.DELTA_STROKE < StyleConsts.MIN_STROKE)
                return;
            Look.FontStroke -= StyleConsts.DELTA_STROKE;
            ApplyFontStroke();
        }

        private void FontColorCycle_Click(object? sender, RoutedEventArgs e)
        {
            Look.FontColor++;
            if (!colorMap.ContainsKey(Look.FontColor))
                Look.FontColor = ColorEnum.White;
            ShowCaption();
        }

        private void BackgroundOpacityIncrease_Click(object? sender, RoutedEventArgs e) =>
            StepBackgroundOpacity(StyleConsts.DELTA_OPACITY);

        private void BackgroundOpacityDecrease_Click(object? sender, RoutedEventArgs e) =>
            StepBackgroundOpacity(-StyleConsts.DELTA_OPACITY);

        private void StepBackgroundOpacity(int delta)
        {
            Look.Opacity = Math.Clamp(Look.Opacity + delta, StyleConsts.MIN_OPACITY, StyleConsts.MAX_OPACITY);
            ApplyBackgroundOpacity();
        }

        private void BackgroundColorCycle_Click(object? sender, RoutedEventArgs e)
        {
            Look.BackgroundColor++;
            if (!colorMap.ContainsKey(Look.BackgroundColor))
                Look.BackgroundColor = ColorEnum.White;
            ApplyBackgroundOpacity();
        }

        private void OnlyModeButton_Click(object? sender, RoutedEventArgs e)
        {
            if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (0) Subtitle + Translation
                OnlyModeButton.Content = "全显";
                OnlyMode = CaptionVisible.Both;
            }
            else if (onlyMode == CaptionVisible.Both)
            {
                // (1) Translation Only
                OnlyModeButton.Content = "译文";
                OnlyMode = CaptionVisible.TranslationOnly;
            }
            else
            {
                // (2) Subtitle Only
                OnlyModeButton.Content = "原文";
                OnlyMode = CaptionVisible.SubtitleOnly;
            }
        }

        private void SwitchModeButton_Click(object? sender, RoutedEventArgs e)
        {
            if (SwitchMode == CaptionLocation.TranslationTop)
            {
                Grid.SetRow(TranslatedCaptionCard, 1);
                Grid.SetRow(OriginalCaptionCard, 0);
                SwitchMode = CaptionLocation.SubtitleTop;
            }
            else
            {
                Grid.SetRow(TranslatedCaptionCard, 0);
                Grid.SetRow(OriginalCaptionCard, 1);
                SwitchMode = CaptionLocation.TranslationTop;
            }
        }

        public void ResizeForOnlyMode()
        {
            double delta = StyleConsts.DELTA_OVERLAY_HEIGHT;
            // The place the user gave the window moves by this much. The window follows in FitHeight: at once
            // where it has the room for it, and once it has become lower where it has not.
            void MoveTop(double amount)
            {
                int top = userTop ?? CurrentPosition.Y;
                if (userTop == null)
                    raisedTop = top;
                userTop = top + (int)(amount * DesktopScaling);
            }

            // The window is lower by `delta` while one line is hidden (FitHeight sees to the height itself).
            if (onlyMode == CaptionVisible.TranslationOnly)
            {
                // (1) Translation only: the window gets lower from the top, its bottom edge stays where it is.
                OriginalCaptionCard.IsVisible = false;
                MinHeight -= delta;
                MoveTop(delta);
            }
            else if (onlyMode == CaptionVisible.SubtitleOnly)
            {
                // (2) Original only
                OriginalCaptionCard.IsVisible = true;
                MoveTop(-delta);
                TranslatedCaptionCard.IsVisible = false;
            }
            else
            {
                // (0) Both
                TranslatedCaptionCard.IsVisible = true;
                MinHeight += delta;
            }
            // (The mode is changed with a button of the window: it is not being dragged at this moment.)
            userMove.Stop();
            FitHeight();
        }

        public void ApplyFontStroke()
        {
            OriginalCaption.StrokeThickness = Look.FontStroke;
            TranslatedCaption.StrokeThickness = Look.FontStroke;
        }

        public void ApplyBackgroundOpacity()
        {
            var color = BackgroundColor;
            BorderBackground.Background = new SolidColorBrush(
                Avalonia.Media.Color.FromArgb((byte)Math.Clamp(Look.Opacity, 0, 255), color.R, color.G, color.B));
        }
    }
}
