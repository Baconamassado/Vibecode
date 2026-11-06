using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Bakobtify.UI;

internal sealed class HoverTips : IDisposable
{
    private readonly Form _owner;
    private readonly Func<int, int> _pixels;
    private readonly Dictionary<Control, string> _texts = [];
    private readonly System.Windows.Forms.Timer _timer = new();
    private readonly Bubble _bubble = new() { Visible = false, TabStop = false };
    private Control? _target;
    private long _lastHidden;

    public HoverTips(Form owner, Func<int, int> pixels)
    {
        _owner = owner;
        _pixels = pixels;
        owner.Controls.Add(_bubble);
        owner.Deactivate += Dismiss;
        owner.Resize += Dismiss;
        owner.LocationChanged += Dismiss;
        _timer.Tick += OnTick;
    }

    public void SetToolTip(Control control, string text)
    {
        if (_texts.TryAdd(control, text))
        {
            control.MouseEnter += EnterTarget;
            control.MouseLeave += LeaveTarget;
            control.MouseDown += Dismiss;
            control.VisibleChanged += LeaveTarget;
            control.Disposed += RemoveTarget;
        }
        else if (_texts[control] != text)
        {
            _texts[control] = text;
            if (_target == control && _bubble.Visible) ShowTip(text);
        }
    }

    private void EnterTarget(object? sender, EventArgs e)
    {
        if (sender is not Control control || !_owner.ContainsFocus) return;
        Dismiss(sender, e);
        _target = control;
        _timer.Interval = _lastHidden != 0 && Environment.TickCount64 - _lastHidden < 500 ? 150 : 450;
        _timer.Start();
    }

    private void LeaveTarget(object? sender, EventArgs e)
    {
        if (sender == _target) Dismiss(sender, e);
    }

    private void RemoveTarget(object? sender, EventArgs e)
    {
        LeaveTarget(sender, e);
        if (sender is Control control) _texts.Remove(control);
    }

    private void Dismiss(object? sender, EventArgs e)
    {
        _timer.Stop();
        _target = null;
        if (_bubble.Visible) _lastHidden = Environment.TickCount64;
        _bubble.Hide();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_bubble.Visible || !_owner.ContainsFocus ||
            _target is not { Visible: true } target ||
            !target.ClientRectangle.Contains(target.PointToClient(Cursor.Position)) ||
            !_texts.TryGetValue(target, out var text))
        {
            Dismiss(sender, e);
            return;
        }

        ShowTip(text);
        _timer.Interval = 5000;
        _timer.Start();
    }

    private void ShowTip(string text)
    {
        if (_target is null || string.IsNullOrWhiteSpace(text)) return;
        var gap = _pixels(8);
        var inset = _pixels(12);
        var font = Theme.Font(_pixels(13));
        var textSize = TextRenderer.MeasureText(text, font, Size.Empty,
            TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        var width = Math.Min(textSize.Width + 2 * inset, Math.Min(_pixels(440), _owner.ClientSize.Width - 2 * gap));
        var height = textSize.Height + _pixels(14);
        if (width <= 0 || height + 2 * gap > _owner.ClientSize.Height) return;

        var anchor = _owner.RectangleToClient(_target.RectangleToScreen(_target.ClientRectangle));
        var x = Math.Clamp(anchor.Left + (anchor.Width - width) / 2, gap, _owner.ClientSize.Width - width - gap);
        var y = anchor.Bottom + gap;
        if (y + height + gap > _owner.ClientSize.Height) y = anchor.Top - height - gap;
        y = Math.Clamp(y, gap, _owner.ClientSize.Height - height - gap);

        _bubble.Text = text;
        _bubble.Font = font;
        _bubble.Inset = inset;
        _bubble.Radius = _pixels(8);
        _bubble.SetBounds(x, y, width, height);
        using var outline = Theme.Rounded(_bubble.ClientRectangle, _bubble.Radius);
        _bubble.Region = new Region(outline);
        _bubble.BringToFront();
        _bubble.Invalidate();
        _bubble.Show();
    }

    public void Dispose()
    {
        _owner.Deactivate -= Dismiss;
        _owner.Resize -= Dismiss;
        _owner.LocationChanged -= Dismiss;
        foreach (var control in _texts.Keys)
        {
            control.MouseEnter -= EnterTarget;
            control.MouseLeave -= LeaveTarget;
            control.MouseDown -= Dismiss;
            control.VisibleChanged -= LeaveTarget;
            control.Disposed -= RemoveTarget;
        }
        _texts.Clear();
        _timer.Dispose();
        _bubble.Dispose();
    }

    private sealed class Bubble : Control
    {
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int Inset { get; set; }
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal int Radius { get; set; }

        public Bubble() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Surface);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var border = new Rectangle(0, 0, Width - 1, Height - 1);
            Theme.Outline(e.Graphics, border, Theme.Border, Radius);
            TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -Inset, 0),
                Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
                TextFormatFlags.EndEllipsis);
        }

        protected override void WndProc(ref Message message)
        {
            const int hitTest = 0x0084;
            if (message.Msg == hitTest)
            {
                message.Result = -1; // HTTRANSPARENT: o mouse continua chegando ao controle abaixo.
                return;
            }
            base.WndProc(ref message);
        }
    }
}
