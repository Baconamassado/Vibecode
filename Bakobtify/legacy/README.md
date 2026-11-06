# Bakobtify

Esta é a versão web anterior. Os comandos abaixo partem de `legacy/`. A
biblioteca compartilhada fica em `../data/bakobtify.db`.

Player de música minimalista para o SoundCloud. Backend em Python puro
(somente biblioteca padrão) e front-end em HTML/CSS/JS sem build, sem npm,
sem framework.

```
python run.py --open
```

Abre em `http://127.0.0.1:8000`.

## Requisitos

- Python 3.10+
- `requests` e `beautifulsoup4` (já usados para extrair o `client_id`)
- `selenium` (opcional, só entra se a extração estática falhar)
- Conexão com a internet

Nada além disso. Não há `pip install`, não há `node_modules`, não há bundler.

## Como funciona

O SoundCloud exige um `client_id` em todas as chamadas da `api-v2`. Em vez de
registrar um aplicativo, o Bakobtify pega o identificador que o próprio site
público usa:

1. **cache local** — SQLite, TTL de 24h;
2. **scraping estático** — baixa o HTML do site e procura o `client_id` no
   bundle JavaScript;
3. **scraping via navegador** — se o bundle estiver ofuscado, o Selenium abre
   o site em Edge/Chrome headless e captura o identificador das requisições;
4. **re-extração automática** em respostas 401/403;
5. **modo degradado** — sem `client_id`, a busca para e o player cai para o
   embed oficial do SoundCloud.

O indicador no rodapé da barra lateral mostra o estado em tempo real:
verde = conectado, verde-escuro = cache, vermelho = modo degradado.

### Reprodução

Cada faixa pede `/api/tracks/{id}/stream`, que devolve:

- `progressive` → MP3 direto do CDN, tocado num `<audio>` normal.
- `widget` → **é o caso de praticamente todas as faixas hoje**: o SoundCloud
  descontinuou os transcodes MP3 progressive e serve apenas HLS, que o
  `<audio>` não toca nativamente sem uma biblioteca extra.

Para não adicionar dependência, o fallback usa o player oficial em um iframe de
**2×2 px, invisível**, controlado pela Widget API. O áudio vem do embed, mas a
interface que você vê e usa — play, seek, volume, fila — é toda do Bakobtify.
O selo `EMBED` ao lado do artista indica quando esse caminho está ativo.

## Riscos

A extração do `client_id` é uso não documentado da API. Os Termos de Serviço do
SoundCloud não cobrem esse caminho e o identificador pode ser rotacionado ou
bloqueado a qualquer momento. Medidas de contenção no código:

- intervalo mínimo de 300 ms entre chamadas;
- cache de busca de 60 s;
- sem prefetch de páginas não abertas;
- re-extração automática com limite de tentativas.

Se um dia parar de funcionar, o modo degradado mantém o app de pé.

## Limitações conhecidas

- O `<audio>` nativo só é usado quando existe transcode progressive; hoje o
  SoundCloud entrega HLS para quase tudo, então o áudio passa pelo embed oculto.
- O embed oculto depende de `w.soundcloud.com` e do script `player/api.js`, que
  são carregados do CDN do SoundCloud em tempo de execução.
- O embed exige a permissão `encrypted-media` (EME); sem ela o áudio não
  inicia. O Bakobtify delega `autoplay; encrypted-media; fullscreen;
  picture-in-picture` ao iframe.
- Se a Widget API não conseguir iniciar o áudio, o player oficial aparece
  visível no canto inferior direito como último recurso.
- Sem internet, o app abre mas não busca nem toca.
- Autoplay pode ser bloqueado pelo navegador na primeira faixa; um clique no
  play resolve.

## Arquitetura

```
run.py                    entrada da aplicação
bakobtify/
  config.py               constantes ajustáveis por env
  store.py                SQLite: favoritos, playlists, histórico, cache
  soundcloud.py           client_id, busca, streams
  api.py                  handlers da API
  server.py               HTTP local + estáticos
web/
  index.html
  css/                    base, layout, componentes, player
  js/
    api.js  state.js  data.js  util.js  main.js
    ui/                   search, library, playlist, tracklist, menu, dialog, toast
    player/               engine, widget, queue, player-ui
data/bakobtify.db         criado automaticamente
```

## API local

| Método | Rota | Função |
|---|---|---|
| GET | `/api/health` | estado do client_id |
| GET | `/api/search?q=` | busca de faixas |
| GET | `/api/tracks/{id}/stream` | URL progressive ou fallback |
| GET/POST | `/api/favorites` | listar/adicionar |
| DELETE | `/api/favorites/{id}` | remover |
| GET/POST | `/api/playlists` | listar/criar |
| GET/DELETE | `/api/playlists/{id}` | abrir/excluir |
| POST | `/api/playlists/{id}/tracks` | adicionar faixa |
| DELETE | `/api/playlists/{id}/tracks/{tid}` | remover faixa |
| GET/POST/DELETE | `/api/history` | histórico |

## Atalhos

| Tecla | Ação |
|---|---|
| `Espaço` | play/pause |
| `←` / `→` | voltar/avançar 5 s |
| `↑` / `↓` | volume |
| `M` | mudo |
| `/` | focar busca |

## Configuração

Variáveis de ambiente (todas opcionais):

| Variável | Padrão | Descrição |
|---|---|---|
| `BAKOBTIFY_PORT` | `8000` | porta HTTP |
| `BAKOBTIFY_HOST` | `127.0.0.1` | aceita `127.0.0.1` ou `localhost`, ambos com escuta em `127.0.0.1` |
| `BAKOBTIFY_CLIENT_TTL` | `24` | horas de validade do client_id |

O servidor escuta apenas em localhost. URLs externas não são buscadas a partir
de entrada do usuário.
