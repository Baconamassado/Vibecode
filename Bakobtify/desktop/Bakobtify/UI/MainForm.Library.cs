using Bakobtify.Data;
using System.Drawing.Drawing2D;

namespace Bakobtify.UI;

internal sealed partial class MainForm
{
    private bool IsFavorite(Track track) => _favorites.Exists(t => t.Id == track.Id);

    private void ToggleFavorite(Track track)
    {
        var removing = IsFavorite(track);
        if (removing) _db.RemoveFavorite(track.Id);
        else _db.AddFavorite(track);
        _favorites = _db.ListFavorites();
        if (_route.Page is Page.Favorites or Page.Home) Render();
        UpdatePlayer();
        Notify(removing ? "Faixa removida das músicas curtidas." : "Faixa adicionada às músicas curtidas.");
    }

    private void NewPlaylist(Track? track = null)
    {
        var text = track is null ? "Dê um nome à sua playlist." : $"Adicionar “{track.Title}” a uma nova playlist.";
        var name = Ask("Nova playlist", text, "Criar", input: true);
        if (name is null) return;
        if (_playlists.Exists(p => p.Name == name))
        {
            Notify("Já existe uma playlist com esse nome.");
            return;
        }
        var playlist = _db.CreatePlaylist(name, track);
        ReloadLibrary();
        Navigate(new(Page.Playlist, PlaylistId: playlist.Id));
        Notify(track is null ? "Playlist criada." : "Playlist criada e faixa adicionada.");
    }

    private void DeletePlaylist(PlaylistInfo playlist)
    {
        if (Ask("Excluir playlist", $"Excluir “{playlist.Name}” e sua lista de faixas?", "Excluir") is null) return;
        _db.DeletePlaylist(playlist.Id);
        _db.DeleteMeta(ContinuationKey(playlist.Id));
        _db.DeleteMeta(FeedbackKey(playlist.Id));
        if (_activePlaylistId == playlist.Id) SetPlaylistContext(null, []);
        ReloadLibrary();
        Navigate(new(Page.Home));
        Notify("Playlist excluída.");
    }

    private void ClearHistory()
    {
        if (Ask("Limpar histórico", "Apagar o registro das faixas que você ouviu?", "Limpar") is null) return;
        _db.ClearHistory();
        _relatedRequest?.Cancel();
        _relatedRecommendations.Clear();
        _db.DeleteMeta(RelatedSetting);
        _history = [];
        Render();
        Notify("Histórico limpo.");
    }

    private void ShowTrackMenu(Control anchor, Point point, Track track, long? playlistId = null,
        Action? removeFromQueue = null) =>
        CreateTrackMenu(track, playlistId, removeFromQueue).Show(anchor, point);

    private ContextMenuStrip CreateTrackMenu(Track track, long? playlistId, Action? removeFromQueue = null)
    {
        var menu = CreateMenu();
        MenuItem(menu.Items, "Tocar agora", () =>
        {
            if (playlistId is { } id && _activePlaylistId != id)
            {
                var tracks = _db.PlaylistTracks(id);
                StartQueue(tracks, tracks.FindIndex(item => item.Id == track.Id), id);
                return;
            }
            var existing = _queue.FindIndex(t => t.Id == track.Id);
            if (existing >= 0) PlayAt(existing);
            else
            {
                var next = _index + 1;
                _queue.Insert(next, track);
                for (var i = 0; i < _playedIndices.Count; i++)
                    if (_playedIndices[i] >= next) _playedIndices[i]++;
                PlayAt(next);
            }
        }, "play");
        MenuItem(menu.Items, "Adicionar à fila", () => AddToQueue(track), "queue");
        if (removeFromQueue is not null)
            MenuItem(menu.Items, "Tirar da fila", removeFromQueue, "close");
        if (CanInspectArtist(track))
            MenuItem(menu.Items, "Ver artista", () => OpenArtist(track), "link");
        var favorite = IsFavorite(track);
        MenuItem(menu.Items, favorite ? "Remover das músicas curtidas" : "Curtir faixa",
            () => ToggleFavorite(track), favorite ? "liked" : "heart");
        var playlists = MenuItem(menu.Items, "Adicionar à playlist", glyph: "list");
        var playlistMenu = new ToolStripDropDownMenu { Renderer = menu.Renderer };
        StyleMenu(playlistMenu);
        playlists.DropDown = playlistMenu;
        MenuItem(playlists.DropDownItems, "Nova playlist…", () => NewPlaylist(track), "plus");
        if (_playlists.Count > 0) playlists.DropDownItems.Add(new ToolStripSeparator());
        foreach (var playlist in _playlists)
        {
            MenuItem(playlists.DropDownItems, playlist.Name, () =>
            {
                _db.AddToPlaylist(playlist.Id, track);
                ReloadLibrary();
                Render();
                Notify($"Faixa adicionada a “{playlist.Name}”.");
            }, "list");
        }
        menu.Items.Add(new ToolStripSeparator());
        var linked = _db.GetLocalAudio(track.Id) is not null;
        MenuItem(menu.Items, linked ? "Trocar arquivo de áudio…" : "Vincular arquivo de áudio…", () => ChooseLocalAudio(track), "volume");
        if (linked)
            MenuItem(menu.Items, "Desvincular arquivo de áudio", () =>
            {
                _db.SetLocalAudio(track.Id, null);
                Notify("Arquivo desvinculado.");
            }, "min");
        if (playlistId is { } id)
        {
            MenuItem(menu.Items, "Remover desta playlist", () =>
            {
                _db.RemoveFromPlaylist(id, track.Id);
                ReloadLibrary();
                Render();
                Notify("Faixa removida da playlist.");
            }, "close", destructive: true);
        }
        if (OfficialTrackUri(track) is { } uri)
        {
            MenuItem(menu.Items, "Copiar link do SoundCloud", () =>
            {
                Clipboard.SetText(uri.AbsoluteUri);
                Notify("Link copiado.");
            }, "link");
        }
        return menu;
    }

    private static Uri? OfficialTrackUri(Track? track) =>
        Uri.TryCreate(track?.PermalinkUrl, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host is "soundcloud.com" or "www.soundcloud.com"
            ? uri : null;

    private void ChooseLocalAudio(Track track)
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Vincular arquivo de áudio à faixa",
            Filter = "Arquivos de áudio (*.mp3;*.m4a;*.aac;*.wav)|*.mp3;*.m4a;*.aac;*.wav",
            CheckFileExists = true,
            RestoreDirectory = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _db.SetLocalAudio(track.Id, dialog.FileName);
        if (_current?.Id == track.Id) PlayAt(_index);
        else Notify("Arquivo de áudio vinculado à faixa.");
    }

    private async Task CheckConnectionAsync()
    {
        try
        {
            var id = await _client.ClientIds.GetAsync(_lifetime.Token);
            if (!await _client.ClientIds.ValidateAsync(id, _lifetime.Token))
                throw new SoundCloud.SoundCloudException("O catálogo do SoundCloud não respondeu à consulta.");
            if (_closing) return;
            UpdateStatus();
            Notify("SoundCloud conectado.");
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) when (ex is SoundCloud.SoundCloudException or HttpRequestException or OperationCanceledException)
        {
            var error = SearchError(ex);
            UpdateStatus(error);
            Notify(error);
        }
    }

    private ContextMenuStrip CreateMenu()
    {
        var menu = new ContextMenuStrip { Renderer = new ContextMenuRenderer(_scale) };
        StyleMenu(menu);
        menu.Closed += (_, _) =>
        {
            if (_closing || !IsHandleCreated) menu.Dispose();
            else BeginInvoke(menu.Dispose);
        };
        return menu;
    }

    private void StyleMenu(ToolStripDropDownMenu menu)
    {
        menu.BackColor = Theme.Surface;
        menu.ForeColor = Theme.Text;
        menu.Font = Theme.Font(Pixels(14));
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Padding = new Padding(Pixels(5));
        menu.Opening += (_, _) =>
        {
            var items = menu.Items.OfType<ToolStripMenuItem>().ToArray();
            var width = Pixels(140);
            foreach (var item in items)
            {
                var text = TextRenderer.MeasureText(item.AccessibleName ?? item.Text, menu.Font,
                    Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                width = Math.Max(width, text.Width + Pixels(item.HasDropDownItems ? 64 : 50));
            }
            width = Math.Min(width, Pixels(320));
            // A seta e a janela usam a mesma largura para ancorar o submenu.
            var menuWidth = width + menu.Padding.Horizontal;
            menu.MinimumSize = menu.MaximumSize = new Size(menuWidth, 0);
            foreach (var item in items)
                item.Size = new Size(width, Pixels(32));
        };
        void UpdateShape(object? sender, EventArgs args)
        {
            if (menu.ClientSize.Width <= 0 || menu.ClientSize.Height <= 0) return;
            using var shape = Theme.Rounded(menu.ClientRectangle, Pixels(12));
            menu.Region = new Region(shape);
        }
        menu.SizeChanged += UpdateShape;
        menu.Opening += UpdateShape;
    }

    private ToolStripMenuItem MenuItem(ToolStripItemCollection items, string label, Action? click = null,
        string glyph = "", bool destructive = false)
    {
        var item = new ToolStripMenuItem(label.Replace("&", "&&"))
        {
            AccessibleName = label, Tag = glyph, ForeColor = destructive ? Theme.Pink : Theme.Text,
            AutoSize = false, Height = Pixels(32)
        };
        if (click is not null) item.Click += (_, _) => RunAction(click);
        items.Add(item);
        return item;
    }

    private sealed class ContextMenuRenderer(float scale) : ToolStripRenderer
    {
        private int Px(int value) => (int)Math.Round(value * scale);

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e) =>
            Theme.Fill(e.Graphics, e.ToolStrip.ClientRectangle, Theme.Surface);

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var bounds = Rectangle.Inflate(e.ToolStrip.ClientRectangle, -1, -1);
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.Outline(e.Graphics, bounds, Theme.Border, Px(11));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var bounds = new Rectangle(Point.Empty, e.Item.Size);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var highlighted = e.Item.Enabled && (e.Item.Selected || e.Item.Pressed);
            var color = e.Item.ForeColor == Theme.Pink ? Theme.Pink : Theme.Accent;
            if (highlighted)
            {
                Theme.Fill(e.Graphics, Rectangle.Inflate(bounds, -Px(1), -Px(2)), Theme.Hover, Px(8));
                Theme.Fill(e.Graphics, new Rectangle(Px(5), Px(10), Px(2), bounds.Height - Px(20)), color, Px(1));
            }
            if (e.Item.Tag is string glyph && glyph.Length > 0)
                Icons.Draw(e.Graphics, glyph, new Rectangle(Px(14), (bounds.Height - Px(17)) / 2, Px(17), Px(17)),
                    highlighted || e.Item.ForeColor == Theme.Pink ? color : Theme.Muted);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            var reserved = e.Item is ToolStripMenuItem { HasDropDownItems: true } ? 64 : 50;
            var width = Math.Max(0, e.Item.Width - Px(reserved));
            TextRenderer.DrawText(e.Graphics, e.Item.AccessibleName ?? e.Text, e.TextFont,
                new Rectangle(Px(38), 0, width, e.Item.Height), e.Item.Enabled ? e.Item.ForeColor : Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            if (e.Item is { } item)
                e.ArrowRectangle = new Rectangle(item.Width - Px(21), 0, Px(12), item.Height);
            e.ArrowColor = e.Item?.Selected == true ? Theme.Accent : Theme.Muted;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var separator = new Pen(Theme.Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(separator, Px(12), y, e.Item.Width - Px(12), y);
        }
    }

    private string? Ask(string title, string message, string confirm, bool input = false, bool dismissOnly = false)
    {
        using var dialog = new Form
        {
            Text = title, Font = Theme.Font(14), ForeColor = Theme.Text, BackColor = Theme.Sidebar,
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            ClientSize = new Size(432, dismissOnly ? 220 : input ? 192 : 163)
        };
        var heading = Theme.Label(title, 25, bold: true);
        var description = Theme.Label(message, 14, Theme.Muted);
        heading.SetBounds(21, 13, 390, 41);
        description.TextAlign = ContentAlignment.TopLeft;
        description.SetBounds(23, 60, 384, dismissOnly ? 107 : 50);
        dialog.Controls.AddRange([heading, description]);
        var field = new TextBox
        {
            BackColor = Theme.Surface, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Nome da playlist", MaxLength = 100, Font = Theme.Font(15),
            AccessibleName = "Nome da playlist"
        };
        if (input)
        {
            field.SetBounds(22, 107, 388, 29);
            dialog.Controls.Add(field);
            dialog.ActiveControl = field;
        }
        var ok = new SoftButton { Text = confirm, DialogResult = DialogResult.OK, BackColor = dialog.BackColor };
        StylePrimary(ok);
        ok.SetBounds(314, dialog.ClientSize.Height - 48, 96, 34);
        var cancel = new SoftButton
        {
            Text = "Cancelar", DialogResult = DialogResult.Cancel, ForeColor = Theme.Muted, BackColor = dialog.BackColor
        };
        cancel.SetBounds(214, dialog.ClientSize.Height - 48, 90, 34);
        dialog.Controls.Add(ok);
        if (!dismissOnly) dialog.Controls.Add(cancel);
        dialog.AcceptButton = ok;
        dialog.CancelButton = dismissOnly ? ok : cancel;
        ok.Enabled = !input;
        field.TextChanged += (_, _) => ok.Enabled = field.Text.Trim().Length > 0;
        var result = dialog.ShowDialog(this) == DialogResult.OK ? field.Text.Trim() : null;
        field.Dispose();
        return result;
    }
}
