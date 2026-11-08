namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private const string DiscordApplicationId = "1568065018728026286";
    private const string DiscordPresenceSetting = "discord_presence";
    private const string LocalAccelerationSetting = "desktop_local_acceleration";
    private const string InterfaceModeSetting = "desktop_interface_mode";
    private const string SearchAudiusSetting = "desktop_search_audius";
    private bool _searchAudius;
    private bool _presenceEnabled;
    private bool _normalInterface;
    private float _interfaceMultiplier = 1f;
    private float _interfacePillPosition;
    private readonly System.Windows.Forms.Timer _interfaceAnimation = new() { Interval = 25 };
    private SoftButton? _interfacePill;
    private long _interfaceAnimationStart;
    private float _interfaceAnimationFrom;
    private float _interfaceScaleFrom;

    private void LoadInterfaceMode()
    {
        _normalInterface = _db.GetMeta(InterfaceModeSetting)?.Value == "normal";
        _interfaceMultiplier = _normalInterface ? 1.15f : 1f;
        _interfacePillPosition = _normalInterface ? 1f : 0f;
        _interfaceAnimation.Tick += (_, _) =>
        {
            var progress = Math.Clamp((Environment.TickCount64 - _interfaceAnimationStart) / 220f, 0f, 1f);
            var eased = progress * progress * (3f - 2f * progress);
            var destination = _normalInterface ? 1f : 0f;
            _interfacePillPosition = _interfaceAnimationFrom + (destination - _interfaceAnimationFrom) * eased;
            var scale = _normalInterface ? 1.15f : 1f;
            _interfaceMultiplier = _interfaceScaleFrom + (scale - _interfaceScaleFrom) * eased;
            if (progress == 1f) _interfaceAnimation.Stop();
            Arrange(resizeQueue: progress == 1f);
            if (_interfacePill is { IsDisposed: false }) _interfacePill.Invalidate();
        };
    }

    private void FinishInterfaceResize()
    {
        if (!_interfaceAnimation.Enabled) return;
        _interfaceAnimation.Stop();
        _interfacePillPosition = _normalInterface ? 1f : 0f;
        _interfaceMultiplier = _normalInterface ? 1.15f : 1f;
        Arrange();
    }

    private void LoadPresenceSettings()
    {
        _presenceEnabled = _db.GetMeta(DiscordPresenceSetting)?.Value != "off";
        _audio.LocalAcceleration = _db.GetMeta(LocalAccelerationSetting)?.Value != "off";
    }

    private void BuildSettings(Surface page)
    {
        Span(page, Theme.Label("Configurações", 36, bold: true), 0, 0, 53);
        var main = new Surface(Theme.Background);
        var side = new Surface(Theme.Background);
        page.Controls.AddRange([main, side]);
        var mainHeight = BuildSettingsSections(main);
        BuildAppearanceSettings(side);

        void PositionColumns()
        {
            const int top = 65, gap = 24, sideWidth = 350, sideHeight = 338;
            var width = Units(page.Width);
            var wide = width >= 900;
            var mainWidth = wide ? Math.Min(760, width - gap - sideWidth) : Math.Min(760, width);
            var sideX = wide ? mainWidth + gap : 0;
            var mainY = wide ? top : top + sideHeight + gap;
            Place(side, sideX, top, wide ? sideWidth : mainWidth, sideHeight);
            Place(main, 0, mainY, mainWidth, mainHeight);
            page.Height = Pixels(mainY + mainHeight);
        }

        page.SizeChanged += (_, _) => PositionColumns();
        PositionColumns();
    }

    private int BuildSettingsSections(Surface page)
    {
        var playback = AddSettingsCard(page, "Reprodução", 0, 232, 6);
        AddSettingToggle(playback, "Modo aleatório", 8, () => _shuffle, () => { _shuffle = !_shuffle; UpdatePlayer(); });
        AddSettingToggle(playback, "Silenciar", 44, () => _audio.Muted, ToggleMute);
        AddSettingToggle(playback, "Usar player web quando necessário", 80, () => _allowEmbeddedPlayer,
            ToggleEmbeddedPlayerSetting).Enabled = EmbeddedSoundCloud.Available;
        AddSettingToggle(playback, "Testar player web sem GPU", 116, () => _disableEmbeddedGpu,
            ToggleEmbeddedGpuSetting).Enabled = EmbeddedSoundCloud.Available;
        AddSettingToggle(playback, "Aceleração local", 152, () => _audio.LocalAcceleration, () =>
        {
            var enabled = !_audio.LocalAcceleration;
            _db.SetMeta(LocalAccelerationSetting, enabled ? "on" : "off", double.MaxValue);
            _audio.LocalAcceleration = enabled;
            UpdatePlayer();
        });
        AddSettingToggle(playback, "Fechar WebView após 30 s em pausa", 188, () => _closeIdleEmbeddedPlayer,
            ToggleEmbeddedIdleClose).Enabled = EmbeddedSoundCloud.Available;

        var discord = AddSettingsCard(page, "Discord", 288, 84);
        AddSettingToggle(discord, "Compartilhar o que estou ouvindo", 8, () => _presenceEnabled, () =>
        {
            var enabled = !_presenceEnabled;
            _db.SetMeta(DiscordPresenceSetting, enabled ? "on" : "off", double.MaxValue);
            _presenceEnabled = enabled;
            UpdateClock();
        });
        var discordHint = Theme.Label("Com o Discord desktop aberto, compartilha faixa, artista e capa.", 13, Theme.Muted);
        discordHint.BackColor = Theme.Surface;
        Span(discord, discordHint, 16, 43, 30, 16);

        var network = AddSettingsCard(page, "Conexão", 428, 108);
        var connection = ButtonAt(network, "Consultar conexão", "link", () => _ = CheckConnectionAsync());
        StyleSettingsButton(connection);
        Place(connection, 16, 14, 187, 38);
        var diagnostics = ButtonAt(network, "Diagnóstico", "settings", () => Navigate(new(Page.Diagnostics)));
        StyleSettingsButton(diagnostics);
        Place(diagnostics, 214, 14, 151, 38);
        AddSettingToggle(network, "Buscar no Audius", 61, () => _searchAudius, () =>
        {
            var enabled = !_searchAudius;
            _db.SetMeta(SearchAudiusSetting, enabled ? "on" : "off", double.MaxValue);
            _searchAudius = enabled;
        });

        const int shortcutsTop = 592;
        var shortcutsHeight = ShortcutLabels.Length * 36 + 16;
        var shortcuts = AddSettingsCard(page, "Atalhos de reprodução", shortcutsTop,
            shortcutsHeight, ShortcutLabels.Length);
        for (var i = 0; i < ShortcutLabels.Length; i++)
        {
            var index = i;
            var y = 8 + i * 36;
            var shortcutLabel = Theme.Label(ShortcutLabels[i], 14);
            shortcutLabel.BackColor = Theme.Surface;
            Span(shortcuts, shortcutLabel, 16, y, 35, 194);
            var caption = _shortcuts[i] == Keys.None ? "Sem atalho" :
                !_registeredShortcuts.Contains(i) ? "Em conflito" :
                new KeysConverter().ConvertToString(_shortcuts[i]) ?? "Editar";
            var shortcut = ButtonAt(shortcuts, caption, "", () => EditShortcut(index), ShortcutLabels[i]);
            StyleSettingsButton(shortcut);
            void Position() => Place(shortcut, Units(shortcuts.Width) - 180, y + 3, 164, 29);
            shortcuts.SizeChanged += (_, _) => Position();
            Position();
        }
        var reset = ButtonAt(page, "Redefinir atalhos", "", ResetShortcuts);
        reset.FillColor = Theme.Surface;
        reset.Stroke = Theme.Border;
        var footer = shortcutsTop + 38 + shortcutsHeight;
        Span(page, Theme.Label("Os atalhos funcionam mesmo com o Bakobtify em segundo plano.", 13, Theme.Muted),
            0, footer + 8, 28);
        Place(reset, 0, footer + 43, 161, 37);
        return footer + 98;
    }

    private void BuildAppearanceSettings(Surface page)
    {
        var appearance = AddSettingsCard(page, "Aparência", 0, 134);
        var pill = ButtonAt(appearance, "", "", () =>
        {
            var normal = !_normalInterface;
            _db.SetMeta(InterfaceModeSetting, normal ? "normal" : "smaller", double.MaxValue);
            _normalInterface = normal;
            _interfaceAnimationFrom = _interfacePillPosition;
            _interfaceScaleFrom = _interfaceMultiplier;
            _interfaceAnimationStart = Environment.TickCount64;
            _interfaceAnimation.Start();
        }, "Alternar tamanho da interface");
        _interfacePill = pill;
        pill.BackColor = Theme.Surface;
        pill.HoverColor = Color.Transparent;
        pill.DrawContent = (g, area) =>
        {
            var track = new Rectangle(0, 0, area.Width - 1, area.Height - 1);
            var half = track.Width / 2;
            Theme.Fill(g, track, Theme.Background, 18);
            Theme.Outline(g, track, Theme.Border, 18);
            var x = 3 + (int)Math.Round((half - 2) * _interfacePillPosition);
            Theme.Fill(g, new Rectangle(x, 3, half - 4, track.Height - 6), Theme.Accent, 15);
            Theme.Write(g, "Menor", new Rectangle(0, 0, half, track.Height), Theme.Medium(13),
                _interfacePillPosition < .5f ? Theme.Background : Theme.Text, TextFormatFlags.HorizontalCenter);
            Theme.Write(g, "Normal", new Rectangle(half, 0, half, track.Height), Theme.Medium(13),
                _interfacePillPosition >= .5f ? Theme.Background : Theme.Text, TextFormatFlags.HorizontalCenter);
        };
        Place(pill, 16, 13, 206, 36);
        var hint = Theme.Label("Normal aumenta a interface em 15%.", 13, Theme.Muted);
        hint.BackColor = Theme.Surface;
        Span(appearance, hint, 16, 53, 28, 16);
        AddSettingToggle(appearance, "Barra de rolagem", 88, () => _showScrollbar, () =>
        {
            var enabled = !_showScrollbar;
            _db.SetMeta(ScrollbarSetting, enabled ? "on" : "off", double.MaxValue);
            _showScrollbar = enabled;
            ResizeContent();
        });

        var shortcuts = AddSettingsCard(page, "Navegação rápida", 190, 110);
        AddHint("Ctrl + K   Buscar músicas e artistas", 10);
        AddHint("Alt + ←   Voltar à tela anterior", 40);
        AddHint("Alt + →   Avançar para a próxima tela", 70);

        void AddHint(string text, int y)
        {
            var label = Theme.Label(text, 13);
            label.BackColor = Theme.Surface;
            Span(shortcuts, label, 16, y, 30, 16);
        }
    }

    private Surface AddSettingsCard(Surface page, string title, int top, int height, int rows = 0)
    {
        Span(page, Theme.Label(title, 21, bold: true), 0, top, 34);
        var card = new Surface(Theme.Background);
        page.Controls.Add(card);
        void Position() => Place(card, 0, top + 38, Math.Min(760, Units(page.Width)), height);
        page.SizeChanged += (_, _) => Position();
        Position();
        PaintDesign(card, (g, area) =>
        {
            var bounds = new Rectangle(0, 0, area.Width - 1, area.Height - 1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            Theme.Fill(g, bounds, Theme.Surface, 13);
            Theme.Outline(g, bounds, Theme.Border, 13);
            using var divider = new Pen(Theme.Border);
            for (var i = 1; i < rows; i++)
                g.DrawLine(divider, 16, 7 + i * 36, bounds.Width - 16, 7 + i * 36);
        });
        return card;
    }

    private static void StyleSettingsButton(SoftButton button)
    {
        button.BackColor = Theme.Surface;
        button.FillColor = Theme.Hover;
        button.HoverColor = Theme.Border;
        button.Stroke = Theme.Border;
    }

    private SoftButton AddSettingToggle(Surface card, string text, int y, Func<bool> enabled, Action toggle)
    {
        var label = Theme.Label(text, 14);
        label.BackColor = Theme.Surface;
        Span(card, label, 16, y, 35, 117);
        var button = ButtonAt(card, "", "", () =>
        {
            toggle();
            card.Invalidate(true);
        }, text);
        StyleSettingsButton(button);
        button.Radius = 14;
        button.DrawContent = (g, rect) => Theme.Write(g, enabled() ? "Ligado" : "Desligado", rect,
            Theme.Medium(13), button.Enabled && enabled() ? Theme.Accent : Theme.Muted, TextFormatFlags.HorizontalCenter);
        void Position() => Place(button, Units(card.Width) - 102, y + 3, 86, 29);
        card.SizeChanged += (_, _) => Position();
        Position();
        return button;
    }
}
