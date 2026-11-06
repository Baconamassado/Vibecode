namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private const string DiscordApplicationId = "1568065018728026286";
    private const string DiscordPresenceSetting = "discord_presence";
    private const string LocalAccelerationSetting = "desktop_local_acceleration";
    private bool _presenceEnabled;

    private void LoadPresenceSettings()
    {
        _presenceEnabled = _db.GetMeta(DiscordPresenceSetting)?.Value != "off";
        _audio.LocalAcceleration = _db.GetMeta(LocalAccelerationSetting)?.Value != "off";
    }

    private void ConfigureToolTips()
    {
        _tips.Popup += (_, args) =>
        {
            var font = Theme.Font(Pixels(13));
            var size = TextRenderer.MeasureText(_tips.GetToolTip(args.AssociatedControl), font);
            args.ToolTipSize = new Size(Math.Min(size.Width + Pixels(16), Pixels(440)), size.Height + Pixels(10));
        };
        _tips.Draw += (_, args) =>
        {
            args.Graphics.Clear(Theme.Border);
            Theme.Fill(args.Graphics, Rectangle.Inflate(args.Bounds, -1, -1), Theme.Surface);
            Theme.Write(args.Graphics, args.ToolTipText ?? "", Rectangle.Inflate(args.Bounds, -Pixels(8), 0),
                Theme.Font(Pixels(13)), Theme.Text);
        };
    }

    private void BuildSettings(Surface page)
    {
        Span(page, Theme.Label("Configurações", 36, bold: true), 0, 0, 53);
        Span(page, Theme.Label("Reprodução", 23, bold: true), 0, 68, 36);
        var playback = new Surface(Theme.Surface);
        Span(page, playback, 0, 112, 187);
        AddSettingToggle(playback, "Modo aleatório", 3, () => _shuffle, () => { _shuffle = !_shuffle; UpdatePlayer(); });
        AddSettingToggle(playback, "Silenciar", 39, () => _audio.Muted, ToggleMute);
        AddSettingToggle(playback, "Usar player web quando necessário", 75, () => _allowEmbeddedPlayer,
            ToggleEmbeddedPlayerSetting).Enabled = EmbeddedSoundCloud.Available;
        AddSettingToggle(playback, "Testar player web sem GPU", 111, () => _disableEmbeddedGpu,
            ToggleEmbeddedGpuSetting).Enabled = EmbeddedSoundCloud.Available;
        AddSettingToggle(playback, "Aceleração local", 147, () => _audio.LocalAcceleration, () =>
        {
            var enabled = !_audio.LocalAcceleration;
            _db.SetMeta(LocalAccelerationSetting, enabled ? "on" : "off", double.MaxValue);
            _audio.LocalAcceleration = enabled;
            UpdatePlayer();
        });

        Span(page, Theme.Label("Discord", 23, bold: true), 0, 322, 36);
        var discord = new Surface(Theme.Surface);
        Span(page, discord, 0, 366, 94);
        AddSettingToggle(discord, "Compartilhar o que estou ouvindo", 10, () => _presenceEnabled, () =>
        {
            var enabled = !_presenceEnabled;
            _db.SetMeta(DiscordPresenceSetting, enabled ? "on" : "off", double.MaxValue);
            _presenceEnabled = enabled;
            UpdateClock();
        });
        Span(discord, Theme.Label("Com o Discord desktop aberto, compartilha faixa, artista e capa.", 13, Theme.Muted),
            16, 55, 28, 16);

        Span(page, Theme.Label("Conexão", 23, bold: true), 0, 482, 36);
        var connection = ButtonAt(page, "Consultar conexão", "", () => _ = CheckConnectionAsync());
        connection.FillColor = Theme.Surface;
        Place(connection, 0, 529, 161, 37);
        var diagnostics = ButtonAt(page, "Diagnóstico", "", () => Navigate(new(Page.Diagnostics)));
        diagnostics.FillColor = Theme.Surface;
        Place(diagnostics, 173, 529, 145, 37);

        Span(page, Theme.Label("Atalhos de reprodução", 23, bold: true), 0, 586, 36);
        Span(page, Theme.Label("Funcionam mesmo com o Bakobtify em segundo plano.", 13, Theme.Muted), 0, 626, 28);
        var shortcuts = new Surface(Theme.Surface);
        var shortcutsHeight = ShortcutLabels.Length * 38 + 8;
        Span(page, shortcuts, 0, 664, shortcutsHeight);
        for (var i = 0; i < ShortcutLabels.Length; i++)
        {
            var index = i;
            var y = 4 + i * 38;
            Span(shortcuts, Theme.Label(ShortcutLabels[i], 14), 16, y, 38, 200);
            var caption = _shortcuts[i] == Keys.None ? "Sem atalho" :
                !_registeredShortcuts.Contains(i) ? "Em conflito" :
                new KeysConverter().ConvertToString(_shortcuts[i]) ?? "Editar";
            var shortcut = ButtonAt(shortcuts, caption, "", () => EditShortcut(index), ShortcutLabels[i]);
            shortcut.FillColor = Theme.Hover;
            void Position() => Place(shortcut, Units(shortcuts.Width) - 180, y + 4, 164, 30);
            shortcuts.SizeChanged += (_, _) => Position();
            Position();
        }
        var reset = ButtonAt(page, "Redefinir atalhos", "", ResetShortcuts);
        reset.FillColor = Theme.Surface;
        Place(reset, 0, 676 + shortcutsHeight, 161, 37);
        page.Height = Pixels(731 + shortcutsHeight);
    }

    private SoftButton AddSettingToggle(Surface card, string text, int y, Func<bool> enabled, Action toggle)
    {
        var label = Theme.Label(text, 14, bold: true);
        Span(card, label, 16, y, 39, 117);
        var button = ButtonAt(card, "", "", () =>
        {
            toggle();
            card.Invalidate(true);
        }, text);
        button.FillColor = Theme.Hover;
        button.DrawContent = (g, rect) => Theme.Write(g, enabled() ? "Ligado" : "Desligado", rect,
            Theme.Medium(13), button.Enabled && enabled() ? Theme.Accent : Theme.Muted, TextFormatFlags.HorizontalCenter);
        void Position() => Place(button, Units(card.Width) - 102, y + 4, 86, 31);
        card.SizeChanged += (_, _) => Position();
        Position();
        return button;
    }
}
