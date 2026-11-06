using System.Runtime.InteropServices;

namespace Bakobtify.UI;

internal sealed class TaskbarPlayback : IDisposable
{
    public const int Previous = 101, Toggle = 102, Next = 103;
    public static readonly uint CreatedMessage = RegisterWindowMessage("TaskbarButtonCreated");
    private ITaskbarList3? _taskbar;
    private (bool Previous, bool Play, bool Next, string Glyph)? _last;
    private bool _unavailable;

    public void Reset()
    {
        _last = null;
        _unavailable = false;
    }

    public void Update(nint window, bool previous, bool play, bool next, string glyph)
    {
        var state = (previous, play, next, glyph);
        if (_unavailable || _last == state) return;
        var buttons = new ThumbButton[3];
        try
        {
            if (_taskbar is null)
            {
                _taskbar = (ITaskbarList3)Activator.CreateInstance(
                    Type.GetTypeFromCLSID(new Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), true)!)!;
                Marshal.ThrowExceptionForHR(_taskbar.HrInit());
            }
            buttons[0] = Create(Previous, "prev", "Faixa anterior", previous);
            buttons[1] = Create(Toggle, glyph, glyph switch
            {
                "pause" => "Pausar", "close" => "Cancelar carregamento", _ => "Reproduzir"
            }, play);
            buttons[2] = Create(Next, "next", "Próxima faixa", next);
            var result = _last is null ? _taskbar.ThumbBarAddButtons(window, buttons.Length, buttons) :
                _taskbar.ThumbBarUpdateButtons(window, buttons.Length, buttons);
            Marshal.ThrowExceptionForHR(result);
            _last = state;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            Dispose();
            _unavailable = true;
        }
        finally
        {
            foreach (var button in buttons)
                if (button.Icon != 0) DestroyIcon(button.Icon);
        }
    }

    private static ThumbButton Create(int id, string glyph, string tooltip, bool enabled)
    {
        var size = SystemInformation.IconSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new SolidBrush(Theme.Surface);
        graphics.FillEllipse(background, 0, 0, size.Width, size.Height);
        Icons.Draw(graphics, glyph, Rectangle.Inflate(new Rectangle(Point.Empty, size), -size.Width / 5, -size.Height / 5), Theme.Text);
        return new() { Mask = 14, Id = id, Icon = bitmap.GetHicon(), Tooltip = tooltip, Flags = enabled ? 0 : 1 };
    }

    public void Dispose()
    {
        if (_taskbar is not null) Marshal.ReleaseComObject(_taskbar);
        _taskbar = null;
        Reset();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ThumbButton
    {
        public int Mask, Id, Bitmap;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Tooltip;
        public int Flags;
    }

    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        [PreserveSig] int HrInit();
        void AddTab(nint window);
        void DeleteTab(nint window);
        void ActivateTab(nint window);
        void SetActiveAlt(nint window);
        void MarkFullscreenWindow(nint window, int fullscreen);
        void SetProgressValue(nint window, ulong completed, ulong total);
        void SetProgressState(nint window, int flags);
        void RegisterTab(nint tab, nint owner);
        void UnregisterTab(nint tab);
        void SetTabOrder(nint tab, nint before);
        void SetTabActive(nint tab, nint owner, int reserved);
        [PreserveSig] int ThumbBarAddButtons(nint window, int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
        [PreserveSig] int ThumbBarUpdateButtons(nint window, int count, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] ThumbButton[] buttons);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
