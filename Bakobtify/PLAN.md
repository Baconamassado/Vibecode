# Bakobtify — plano de migração para C# (sem dependências novas)

## Objetivo

Sair do navegador e do servidor Python, entregando um app desktop Windows enxuto
(WinForms) que mantém a biblioteca existente, toca SoundCloud e consome pouca RAM.

## Restrições rígidas

- **Nenhuma dependência nova.** Nada de NuGet, npm, pip, Selenium, WebView2.
- Usar apenas o que já existe na máquina:
  - .NET SDK 10.0.400 + runtime Desktop 10.x (framework-dependent);
  - `C:\Windows\System32\winsqlite3.dll` (SQLite via P/Invoke);
  - Media Foundation (`mfreadwrite`, `mfplat`, `mfsrcsnk`, `MSAudDecMFT`);
  - `winmm.dll` (waveOut) para saída de áudio;
  - GDI+ / WinForms para a interface.
- Build offline: `nuget.config` com `<clear/>` e TFM `net10.0-windows`
  (sem projeções WinRT, que exigiriam pacote).

## Decisões

| Tema | Escolha |
|---|---|
| UI | WinForms, controles desenhados, lista virtualizada |
| Dados | `winsqlite3.dll` + schema e `data/bakobtify.db` atuais |
| HTTP/JSON | `HttpClient` + `System.Text.Json` |
| Áudio | HLS gerenciado → fMP4 local → Media Foundation Source Reader → waveOut |
| client_id | cache SQLite + scraping estático (sem Selenium) |
| Distribuição | framework-dependent (runtime já instalado) |
| Legado | Python/web movidos para `legacy/` ao final |

## Estrutura

```
desktop/
  nuget.config                 fontes desabilitadas (garante build offline)
  Bakobtify.sln
  Bakobtify/
    Bakobtify.csproj
    Program.cs
    AppConfig.cs
    Data/       Sqlite.cs (P/Invoke), Database.cs, Track.cs
    SoundCloud/ ClientIdProvider.cs, SoundCloudClient.cs, Models.cs
    Playback/   HlsManifest.cs, HlsDownloader.cs, MfDecoder.cs, WaveOutPlayer.cs, AudioEngine.cs, Queue.cs
    UI/         MainForm.cs, Theme.cs, PlayerBar.cs, Sidebar.cs, QueuePanel.cs
    UI/Controls SeekBar.cs, VolumeBar.cs, TrackList.cs, Toast.cs
    UI/Views/   SearchView.cs, FavoritesView.cs, HistoryView.cs, PlaylistView.cs
```

## Etapas

### 1. Slider da versão web (patch isolado)
- Helper único de arrasto: `pointermove/up/cancel` no `window` enquanto arrasta,
  `lostpointercapture`, `blur` e `visibilitychange` encerram o estado.
- Fim do `previewing` preso; tempo/progresso nunca congelam.
- Duração recuperada do widget antes de soltar; re-sync após `seekTo`.
- `user-select: none` no player; teclado no slider sem conflito com atalhos globais.

### 2. Spike de áudio (gate)
- Ler `client_id` do SQLite; completar por scraping estático se expirado.
- Resolver uma faixa real, baixar o HLS e decodificar via MF para PCM.
- Validar: samples > 0, waveOut abre, seek funciona, RAM medida.
- Se o Source Reader abrir o `.m3u8` remoto, dispensar o downloader.

### 3. Núcleo
- `Sqlite.cs`: `sqlite3_open_v2/prepare_v2/step/bind_*/column_*/finalize/close_v2`.
- `Database.cs`: mesmas tabelas e consultas do `store.py`.
- `SoundCloudClient`: headers, throttle 300 ms, cache de busca, retry 401/403,
  normalização de faixa, resolução `progressive`/`hls`.

### 4. Áudio + fila
- Manifesto HLS (EXTINF, EXT-X-MAP, EXT-X-KEY/AES-128, playlist mestre).
- Download para temp + índice tempo↔segmento; limpeza ao trocar de faixa.
- Source Reader configurado para PCM 16-bit estéreo; volume por escala de amostras.
- waveOut com buffers e evento; posição por amostras.
- Seek recria o reader no segmento alvo (ou decodifica a faixa para WAV temporário).
- Fila, repeat, shuffle, next/prev, ended, erros → toast.

### 5. Interface
- MainForm com sidebar, topbar de busca, view, painel de fila e player bar.
- `SeekBar` próprio: `Capture`, commit no `MouseUp`, reset em
  `MouseCaptureChanged`/`LostFocus`, timer de 200 ms lendo a posição real.
- Favoritas, playlists, histórico, status do client_id, atalhos, menu da faixa.
- Capas baixadas e reduzidas a 64 px, cache LRU.

### 6. Fechamento
- Medir WorkingSet em idle e tocando; ajustar caches.
- README novo; mover Python/web para `legacy/`; manter `data/` compartilhado.

## Critérios de aceite

- App abre sem navegador, sem porta HTTP e sem processo Python.
- Busca, favoritas, playlists e histórico continuam funcionando com o banco atual.
- Áudio toca do início ao fim, volume/mudo funcionam e o seek responde em qualquer
  arrasto (sem travar).
- Build e execução sem baixar nenhum pacote.

## Riscos

- HLS sem handler nativo no Windows: coberto pelo downloader + Source Reader.
- `winsqlite3.dll` antigo: schema usa recursos suportados desde SQLite 3.24.
- Faixas com proteção extra: erro claro e link para abrir no SoundCloud.
