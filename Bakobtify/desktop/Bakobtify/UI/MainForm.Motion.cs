namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private const string ScrollbarSetting = "desktop_show_scrollbar";
    private readonly System.Windows.Forms.Timer _motion = new() { Interval = 16 };
    private readonly List<(Control Control, long Start, int Duration, Action<float> Frame)> _animations = [];
    private readonly Surface _scrollbar = new(Theme.Background) { Cursor = Cursors.Hand, Visible = false };
    private bool _showScrollbar;
    private int _scroll, _scrollTarget, _scrollFrom, _wheelRemainder;
    private long _scrollStart;
    private int _pageOffset;
    private int? _dragAnchor;
    private bool _homeRenderPending;
    private Label? _greeting;
    private string? _recentShelfKey, _discoverShelfKey;

    private int MaxScroll => Math.Max(0, (_content?.Height ?? 0) + Pixels(51) - _viewport.ClientSize.Height);
    private static float EaseOut(float progress) => 1 - MathF.Pow(1 - progress, 3);
    private static string GreetingAt(int hour) => hour < 12 ? "Bom dia" : hour < 18 ? "Boa tarde" : "Boa noite";

    private void InitializeMotion()
    {
        _showScrollbar = _db.GetMeta(ScrollbarSetting)?.Value == "on";
        _viewport.Controls.Add(_scrollbar);
        _motion.Tick += (_, _) => AdvanceMotion();
        _scrollbar.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var thumb = ScrollThumb();
            var rail = new Rectangle(thumb.X, 0, thumb.Width, _scrollbar.Height);
            Theme.Fill(e.Graphics, rail, Theme.Surface, thumb.Width / 2);
            Theme.Fill(e.Graphics, thumb, _dragAnchor.HasValue ? Theme.Accent :
                _scrollbar.ClientRectangle.Contains(_scrollbar.PointToClient(Cursor.Position)) ? Theme.Muted :
                Color.FromArgb(96, 90, 94), thumb.Width / 2);
        };
        _scrollbar.MouseEnter += (_, _) => _scrollbar.Invalidate();
        _scrollbar.MouseLeave += (_, _) => _scrollbar.Invalidate();
        _scrollbar.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            var thumb = ScrollThumb();
            _dragAnchor = thumb.Contains(e.Location) ? e.Y - thumb.Top : thumb.Height / 2;
            _scrollbar.Capture = true;
            DragScrollbar(e.Y);
        };
        _scrollbar.MouseMove += (_, e) => DragScrollbar(e.Y);
        _scrollbar.MouseUp += (_, e) => { if (e.Button == MouseButtons.Left) _scrollbar.Capture = false; };
        _scrollbar.MouseCaptureChanged += (_, _) => { _dragAnchor = null; _scrollbar.Invalidate(); };
    }

    private Rectangle ScrollThumb()
    {
        var height = _scrollbar.Height;
        var width = Math.Max(3, Pixels(5));
        var visible = _viewport.ClientSize.Height;
        var length = Math.Clamp((int)(height * (double)visible / Math.Max(1, visible + MaxScroll)),
            Math.Min(height, Pixels(32)), height);
        var top = MaxScroll == 0 ? 0 : (int)Math.Round((height - length) * (double)_scroll / MaxScroll);
        return new Rectangle((_scrollbar.Width - width) / 2, top, width, length);
    }

    private void DragScrollbar(int y)
    {
        if (_dragAnchor is not int anchor) return;
        var travel = Math.Max(1, _scrollbar.Height - ScrollThumb().Height);
        SetScroll((int)Math.Round(Math.Clamp((y - anchor) / (double)travel, 0, 1) * MaxScroll));
        _scrollbar.Invalidate();
    }

    private void SetScroll(int value)
    {
        _scrollTarget = _scroll = Math.Clamp(value, 0, MaxScroll);
        PositionScrolledContent();
    }

    private void PositionScrolledContent()
    {
        if (_content is not null) _content.Top = Pixels(31) - _scroll + _pageOffset;
        if (_scrollbar.Visible) _scrollbar.Invalidate();
    }

    private void ScrollTowards(int value)
    {
        var target = Math.Clamp(value, 0, MaxScroll);
        if (target == _scrollTarget) return;
        _scrollFrom = _scroll;
        _scrollTarget = target;
        _scrollStart = Environment.TickCount64;
        _motion.Start();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_viewport.ClientRectangle.Contains(_viewport.PointToClient(Cursor.Position)) && MaxScroll > 0)
        {
            var lines = SystemInformation.MouseWheelScrollLines;
            _wheelRemainder += e.Delta;
            var steps = _wheelRemainder / 120;
            _wheelRemainder %= 120;
            var distance = lines < 0 ? _viewport.ClientSize.Height : Pixels(27) * lines;
            ScrollTowards(_scrollTarget - steps * distance);
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            return;
        }
        base.OnMouseWheel(e);
    }

    private void TrackPageFocus(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child.TabStop) child.Enter += (_, _) =>
            {
                var top = _viewport.PointToClient(child.PointToScreen(Point.Empty)).Y;
                var bottom = top + child.Height;
                if (top < 0) SetScroll(_scroll + top - Pixels(8));
                else if (bottom > _viewport.Height) SetScroll(_scroll + bottom - _viewport.Height + Pixels(8));
            };
            TrackPageFocus(child);
        }
    }

    private void Animate(Control control, int duration, Action<float> frame, int delay = 0)
    {
        _animations.Add((control, Environment.TickCount64 + delay, duration, frame));
        frame(0);
        _motion.Start();
    }

    private void AdvanceMotion()
    {
        var now = Environment.TickCount64;
        if (_scroll != _scrollTarget)
        {
            var progress = EaseOut(Math.Clamp((now - _scrollStart) / 150f, 0, 1));
            _scroll = _scrollFrom + (int)Math.Round((_scrollTarget - _scrollFrom) * progress);
            PositionScrolledContent();
        }
        for (var i = _animations.Count - 1; i >= 0; i--)
        {
            var animation = _animations[i];
            if (animation.Control.IsDisposed) { _animations.RemoveAt(i); continue; }
            if (now < animation.Start) continue;
            var progress = Math.Clamp((now - animation.Start) / (float)animation.Duration, 0, 1);
            animation.Frame(progress);
            if (progress == 1) _animations.RemoveAt(i);
        }
        if (_scroll != _scrollTarget || _dragAnchor.HasValue) return;
        if (_homeRenderPending)
        {
            _homeRenderPending = false;
            if (_route.Page == Page.Home) Render();
        }
        if (_animations.Count == 0) _motion.Stop();
    }

    private void AnimatePage()
    {
        if (_content is not { } page) return;
        Animate(page, 170, progress =>
        {
            _pageOffset = (int)Math.Round(Pixels(9) * (1 - EaseOut(progress)));
            PositionScrolledContent();
        });
    }

    private void UpdateGreeting()
    {
        if (_greeting is not ThemeLabel { IsDisposed: false } label) return;
        var greeting = GreetingAt(DateTime.Now.Hour);
        if (greeting == label.Text || Equals(label.Tag, greeting)) return;
        _animations.RemoveAll(animation => animation.Control == label);
        label.Tag = greeting;
        Animate(label, 300, progress =>
        {
            if (progress >= .5f) label.Text = greeting;
            label.TextOpacity = Math.Abs(2 * progress - 1);
            label.Invalidate();
        });
    }
}
