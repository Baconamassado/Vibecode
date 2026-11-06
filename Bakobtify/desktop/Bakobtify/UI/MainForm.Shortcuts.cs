using System.Runtime.InteropServices;
using System.Text.Json;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private const string ShortcutSetting = "desktop_music_shortcuts";
    private const int FirstHotkey = 201;
    private static readonly string[] ShortcutLabels =
    [
        "Reproduzir ou pausar", "Faixa anterior", "Próxima faixa", "Voltar 5 segundos",
        "Avançar 5 segundos", "Diminuir volume", "Aumentar volume", "Silenciar ou reativar som"
    ];
    private readonly Keys[] _shortcuts = new Keys[ShortcutLabels.Length];
    private readonly HashSet<int> _registeredShortcuts = [];

    private void LoadShortcuts()
    {
        if (_db.GetMeta(ShortcutSetting)?.Value is not { } value) return;
        try
        {
            if (JsonSerializer.Deserialize<Keys[]>(value) is not { } saved) return;
            for (var i = 0; i < Math.Min(saved.Length, _shortcuts.Length); i++)
                if (ValidShortcut(saved[i]) && !_shortcuts.Contains(saved[i])) _shortcuts[i] = saved[i];
        }
        catch (JsonException) { }
    }

    private void RegisterShortcuts()
    {
        for (var i = 0; i < _shortcuts.Length; i++)
            if (_shortcuts[i] != Keys.None) RegisterShortcut(i, _shortcuts[i]);
    }

    private bool RegisterShortcut(int index, Keys keys)
    {
        const uint NoRepeat = 0x4000;
        var modifiers = NoRepeat;
        if (keys.HasFlag(Keys.Alt)) modifiers |= 1;
        if (keys.HasFlag(Keys.Control)) modifiers |= 2;
        if (keys.HasFlag(Keys.Shift)) modifiers |= 4;
        if (!RegisterHotKey(Handle, FirstHotkey + index, modifiers, (uint)(keys & Keys.KeyCode))) return false;
        _registeredShortcuts.Add(index);
        return true;
    }

    private void UnregisterShortcuts()
    {
        foreach (var index in _registeredShortcuts) UnregisterHotKey(Handle, FirstHotkey + index);
        _registeredShortcuts.Clear();
    }

    private static bool ValidShortcut(Keys keys)
    {
        var key = keys & Keys.KeyCode;
        return (keys & (Keys.Control | Keys.Alt)) != 0 &&
            (key is >= Keys.A and <= Keys.Z or >= Keys.D0 and <= Keys.D9 or >= Keys.F1 and <= Keys.F24
                or Keys.Space or Keys.Left or Keys.Right or Keys.Up or Keys.Down) &&
            keys != (Keys.Control | Keys.K) && keys != (Keys.Alt | Keys.Left) && keys != (Keys.Alt | Keys.Right);
    }

    private void EditShortcut(int index)
    {
        using var dialog = new ShortcutCapture(ShortcutLabels[index]);
        UnregisterShortcuts();
        try
        {
            if (dialog.ShowDialog(this) != DialogResult.OK || dialog.Selected == _shortcuts[index]) return;
            if (_shortcuts.Contains(dialog.Selected) || !RegisterShortcut(index, dialog.Selected))
            {
                Notify("Esse atalho já está em uso. Escolha outra combinação.");
                return;
            }
            var updated = (Keys[])_shortcuts.Clone();
            updated[index] = dialog.Selected;
            _db.SetMeta(ShortcutSetting, JsonSerializer.Serialize(updated), double.MaxValue);
            _shortcuts[index] = dialog.Selected;
        }
        finally
        {
            UnregisterShortcuts();
            RegisterShortcuts();
            Render();
        }
    }

    private void ResetShortcuts()
    {
        _db.DeleteMeta(ShortcutSetting);
        UnregisterShortcuts();
        Array.Clear(_shortcuts);
        Render();
        Notify("Atalhos redefinidos.");
    }

    private void RunShortcut(int index)
    {
        switch (index)
        {
            case 0 when _play.Enabled: TogglePlayback(); break;
            case 1 when _prev.Enabled: Previous(); break;
            case 2 when _next.Enabled: Advance(false); break;
            case 3: SeekPlayback(PlaybackPosition - TimeSpan.FromSeconds(5)); break;
            case 4: SeekPlayback(PlaybackPosition + TimeSpan.FromSeconds(5)); break;
            case 5: SetVolume(_audio.Volume - .05); break;
            case 6: SetVolume(_audio.Volume + .05); break;
            case 7: ToggleMute(); break;
        }
    }

    private sealed class ShortcutCapture : Form
    {
        private readonly Label _instructions = Theme.Label("Pressione Ctrl ou Alt com uma tecla. Esc cancela.", 14, Theme.Muted);
        public Keys Selected { get; private set; }

        public ShortcutCapture(string action)
        {
            Text = action;
            BackColor = Theme.Sidebar;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = MinimizeBox = MaximizeBox = false;
            ClientSize = new Size(440, 105);
            var title = Theme.Label(action, 22, bold: true);
            title.SetBounds(18, 10, 404, 36);
            _instructions.SetBounds(18, 48, 404, 44);
            Controls.AddRange([title, _instructions]);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keys)
        {
            if (keys == Keys.Escape) DialogResult = DialogResult.Cancel;
            else if (ValidShortcut(keys))
            {
                Selected = keys;
                DialogResult = DialogResult.OK;
            }
            else _instructions.Text = "Use Ctrl ou Alt com uma letra, número, seta, Espaço ou F1–F24.";
            return true;
        }
    }

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint window, int id);
}
