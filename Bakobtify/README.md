# Bakobtify

Player de música para Windows com SoundCloud e Audius. O aplicativo desktop usa C# e
WinForms com o .NET 10 Desktop x64. O áudio é decodificado pelo Media Foundation
e enviado ao dispositivo padrão do Windows.

## Em breve

O desenvolvimento da versão sem WebView e a integração com mais provedores de
música, incluindo Spotify e YouTube, estão pausados pelos seguintes motivos.

- Muito complicado
- Preguiça

O Bakobtify continuará recebendo melhorias de qualidade de vida, correções de
bugs e novas funcionalidades.

## Usar a versão empacotada

Extraia o ZIP inteiro para uma pasta gravável e execute `Bakobtify.exe`.
O pacote requer Windows x64 e .NET Desktop Runtime 10 x64. O player web
integrado também requer o Microsoft Edge WebView2 Runtime.

O Bakobtify cria seu banco local na primeira execução. Para abrir o painel
de reprodução e conexão, vá a **Configurações → Diagnóstico**.

## Abrir pelo projeto

No PowerShell, na raiz do projeto:

```powershell
dotnet build .\desktop\Bakobtify\Bakobtify.csproj -p:RestoreConfigFile=desktop/nuget.config
.\desktop\Bakobtify\bin\Debug\net10.0-windows\Bakobtify.exe
```

A configuração do NuGet em `desktop/nuget.config` deixa as fontes de pacotes
desabilitadas. O projeto usa o SDK e o runtime Desktop já instalados.

## Biblioteca e reprodução

- **Início** mostra atalhos da biblioteca, faixas ouvidas e sugestões do catálogo.
- **Explorar** reúne filtros, gêneros e uma faixa em destaque.
- A busca combina os catálogos disponíveis, dá prioridade a correspondências por
  título e artista e aceita links públicos de faixas do SoundCloud.
- O menu de cada faixa permite curtir, adicionar a uma playlist ou fila e copiar
  seu link. As playlists e o histórico têm ações próprias na tela da coleção.
- Use os controles do player para voltar, avançar, arrastar a posição, alternar
  repetição, abrir a fila e controlar o volume. O modo aleatório fica no menu de
  configurações e na tela da coleção.

## Catálogos e privacidade

O Audius usa a API pública. A busca nele vem desligada por padrão e pode ser
ativada em **Configurações → Conexão → Buscar no Audius**. O menu da faixa abre
sua página no Audius.

A busca envia o texto digitado aos catálogos habilitados. A reprodução consulta
o provedor da faixa. Favoritos, playlists e histórico ficam no banco local, e
o áudio do Audius usa arquivos temporários durante a reprodução.

| Atalho | Ação |
| --- | --- |
| Ctrl + K | Focar a busca |
| Espaço | Reproduzir ou pausar |
| ← / → | Voltar ou avançar 5 s |
| ↑ / ↓ | Ajustar volume |
| M | Alternar mudo |
| Alt + ← / Alt + → | Voltar ou avançar entre telas |

As playlists, as curtidas e o histórico continuam no SQLite
`data/bakobtify.db`. Use `BAKOBTIFY_DB` para indicar outro arquivo, se necessário.
A busca, as capas e os streams dependem do SoundCloud. Quando o catálogo só
oferece uma prévia compatível, o player identifica o trecho como **Prévia**.
Para tocar a versão completa de uma faixa usando um arquivo que você já tem,
selecione **Vincular arquivo de áudio** no menu da faixa. O aplicativo guarda
o caminho no banco local e usa o mesmo player nativo para tocar o arquivo.

Se a reprodução nativa falhar, o desktop usa o player oficial do SoundCloud
em um WebView2 oculto, comandado pelos controles do Bakobtify. O WebView2 é
carregado sob demanda e é fechado ao trocar de faixa. A compilação usa os
componentes WebView2 já presentes no Power Query desta máquina. Em outra
instalação, `BakobtifyWebView2Dir` pode apontar para uma pasta x64 que já
contenha as duas DLLs gerenciadas e o `WebView2Loader.dll`.

Por padrão, após 30 segundos em pausa, o Bakobtify fecha o WebView2 e guarda a posição
para retomar. Desative **Fechar WebView após 30 s em pausa** em
**Configurações → Reprodução** para manter o player aberto. Para usar somente
o motor nativo, desative **Usar player web quando necessário**.

Com o Discord desktop aberto, vá à seção **Discord** em **Configurações** e ative **Compartilhar o que estou ouvindo** para exibir faixa, artista e capa.
A atividade é removida ao pausar ou desligar a integração.

A seção **Para você** ordena o catálogo já consultado no app usando os artistas
e gêneros do histórico local. Escutas mais recentes pesam mais, e faixas ainda
não ouvidas recebem prioridade.

O código da versão web anterior está em `legacy/`.
