using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace Bakobtify.UI;

internal sealed class Slider : Control
{
    private double _value;
    private double _preview;
    public bool Dragging { get; private set; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool Live { get; init; }
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal float PaintScale { get; set; } = 1;
    public double Preview => Dragging ? _preview : _value;
    public event Action<double>? Committed;
    public event Action? PreviewChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal double Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = Math.Clamp(value, 0, 1);
            if (!Dragging) Invalidate();
        }
    }

    public Slider()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        BackColor = Theme.Sidebar;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.Slider;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var area = Theme.DesignArea(g, ClientSize, PaintScale);
        var length = Math.Max(1, area.Width - 8);
        var y = area.Height / 2;
        Theme.Fill(g, new Rectangle(4, y - 2, length, 4), Color.FromArgb(49, 49, 53), 2);
        var amount = (int)Math.Round(length * Preview);
        Theme.Fill(g, new Rectangle(4, y - 2, amount, 4), Enabled ? Theme.Text : Theme.Muted, 2);
        if (Enabled && (Dragging || Focused || ClientRectangle.Contains(PointToClient(Cursor.Position))))
            Theme.Fill(g, new Rectangle(amount, y - 4, 8, 8), Theme.Text, 4);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        Focus();
        Dragging = true;
        Capture = true;
        UpdatePreview(e.X);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Dragging) UpdatePreview(e.X);
        else Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!Dragging || e.Button != MouseButtons.Left) return;
        UpdatePreview(e.X);
        var value = _preview;
        EndDrag();
        if (!Live) Commit(value);
    }

    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) EndDrag(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); EndDrag(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) EndDrag(); Invalidate(); }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down
        or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var value = e.KeyCode switch
        {
            Keys.Left or Keys.Down => Value - .05,
            Keys.Right or Keys.Up => Value + .05,
            Keys.Home => 0,
            Keys.End => 1,
            _ => (double?)null
        };
        if (value is not { } result) return;
        Commit(Math.Clamp(result, 0, 1));
        e.Handled = true;
    }

    private void UpdatePreview(int x)
    {
        var inset = 4 * PaintScale;
        _preview = Math.Clamp((x - inset) / Math.Max(1, Width - 2 * inset), 0, 1);
        if (Live) Commit(_preview);
        PreviewChanged?.Invoke();
        Invalidate();
    }

    private void EndDrag()
    {
        if (!Dragging) return;
        Dragging = false;
        Capture = false;
        PreviewChanged?.Invoke();
        Invalidate();
    }

    private void Commit(double value)
    {
        Value = value;
        Committed?.Invoke(value);
    }
}
