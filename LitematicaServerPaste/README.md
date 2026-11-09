# Litematica Server Paste (LSP)

Plugin para **Paper 26.1.2** e **Litematica 0.27.9 normal** no cliente. Recebe o
paste pelo protocolo Servux e aplica estados de blocos e NBT no servidor,
incluindo os inventários recebidos no schematic.

**Primeira implementação: não compilada e não testada em servidor.**
O usuário fará a compilação em outro dispositivo. Os testes automatizados foram
escritos, mas não executados. **Ainda não há release pronto nem JAR publicado.**

## Compilar no outro dispositivo

Usar JDK 25 instalado e Gradle 9.1.0 ou posterior compatível com Java 25.
Na pasta do projeto:

```text
gradle build
```

O comando baixa as dependências declaradas, compila e executa os testes.
Saída esperada: `build/libs/LitematicaServerPaste-0.1.0.jar`.
Não há Gradle Wrapper nem download automático de JDK neste projeto.

Dependências: Paper API `26.1.2.build.74-stable` para compilação e JUnit 5.12.2
para testes. Nenhuma biblioteca adicional precisa ser instalada como plugin.
A ponte para NBT usa reflexão em assinaturas verificadas nos fontes do Paper
26.1.2; seu funcionamento ainda depende de teste real.

## Usar

1. Instalar o jar em `plugins/` do Paper 26.1.2 e iniciar o servidor.
2. Conectar com Litematica 0.27.9 e suas dependências habituais.
3. Habilitar `entityDataSync=true` e `pasteUsingServux=true` em Generic.
   Reconectar se o cliente já estava conectado quando o plugin foi instalado.
4. Usar modo criativo com permissão `lsp.paste` (padrão: operadores).
5. Posicionar a construção e executar o paste normal do Litematica.
6. Conferir `/lsp status` e a mensagem de conclusão.

Todos os chunks de destino precisam estar carregados. O plugin reserva tickets
temporários nos chunks aceitos e não carrega nem gera intencionalmente chunks
ausentes.

O LSP respeita `pasteReplaceBehavior` e as camadas quando
`pasteLayerBehavior=rendered_only`. Com `none`, blocos existentes permanecem.
Para restaurar um baú já colocado, usar `all` ou `with_non_air`: seu NBT é
restaurado mesmo quando o estado do bloco já coincide.

Os dados precisam existir no schematic. Criar o arquivo no singleplayer é uma
forma de incluir inventários completos; o plugin não recupera itens ausentes.

## Comandos

- `/lsp help`: configuração.
- `/lsp status`: protocolo e andamento.
- `/lsp cancel`: cancela recebimento, validação ou colagem.

## Implementação

- Protocolo 1 da 0.27.9, com identificação por string e transaction ID.
- Caminho adicional para protocolo 2 da linha LTS/26.1, com frame, gzip e NBT
  nomeado; ainda não validado com um cliente real.
- Paletas com bits contínuos cruzando palavras de 64 bits.
- Dimensões negativas, sub-regiões, origem, rotação, espelhamento e camadas.
- Ajustes geométricos de orientação, tipos de baú duplo e formas de escadas.
- NBT aplicado por snapshots do Paper, incluindo componentes de itens e dados
  de diferentes tipos de entidades de bloco.
- Validação de estados e NBT antes da primeira alteração no mundo.
- Decodificação fora da thread principal; preparação e colagem na thread
  principal, com orçamento global de operações e tempo por tick.
- Limites de bytes, descompressão, profundidade, nós NBT, blocos, paletas, chunks,
  sessões e tempo; colagens simultâneas não podem compartilhar chunks.
- Liberação de sessões/tickets ao cancelar, desconectar, trocar de mundo,
  sair do criativo, perder permissão, exceder timeout ou desativar o plugin.

## Limitações desta versão

- Conceda `lsp.paste` somente a administradores de confiança. A permissão permite aplicar NBT arbitrário, incluindo blocos de comando, sem consultar proteções de região.

- Entidades são recriadas por snapshots do Paper após os blocos, com novos UUIDs
  e passageiros remontados. Para colar só blocos, ativar **Ignore Entities**
  na sub-região. A opção Generic `pasteIgnoreEntities` não é transmitida por
  esse caminho na 0.27.9. Vínculos por UUID, como certas coleiras, e coordenadas
  internas específicas de mobs ainda precisam de tratamento próprio.
- Ticks agendados de bloco e fluido são submetidos após a colagem, com o atraso,
  prioridade e ordem do schematic. Só entram ticks de posições efetivamente
  coladas cujo tipo ainda corresponde ao tick. O servidor pode consolidar
  ticks duplicados.
- A supressão local de física, redstone, fluidos, pistões e operações de
  inventário não congela todos os ticks. Fornalhas, command blocks e outros
  blocos ativos podem executar entre lotes. Não há equivalência garantida ao
  servidor integrado. Uma chamada à API pode ultrapassar o orçamento por tick.
- Não oferece overlays NBT, salvamento remoto, Easy Place ou outros serviços
  Servux. Não combinar com outro provedor do canal `servux:litematics`.
- Não possui undo automático nem integração com proteção de regiões.
  Cancelamento ou erro durante a colagem mantém as alterações já aplicadas.
- Exige a mesma versão de dados Minecraft do servidor; não converte NBT antigo
  por conta própria.
- Redstone, todos os tipos de blocos e compatibilidade com outros plugins
  precisam de validação no servidor real.

## Validação

Os testes de `src/test/java` usam fixtures sintéticas baseadas nos formatos
oficiais e cobrem rede, NBT inválido, tipos dos dados, paletas e coordenadas.
Eles não comprovam integração real com Paper/Litematica.
Roteiro de servidor: [docs/VALIDACAO.md](docs/VALIDACAO.md).

## Referências

- [Litematica 26.1.2-0.27.9](https://github.com/sakura-ryoko/litematica/tree/26.1.2-0.27.9),
  commit do tag: `dea194794a0ccd2fe740748eeb2a474dafec49e1`.
- [Servux LTS/26.1](https://github.com/sakura-ryoko/servux/tree/LTS/26.1).
- [CraftBlockEntityState do Paper 26.1.2](https://github.com/PaperMC/Paper/blob/ver/26.1.2/paper-server/src/main/java/org/bukkit/craftbukkit/block/CraftBlockEntityState.java).
- [Serializador NBT do Paper 26.1.2](https://github.com/PaperMC/Paper/blob/ver/26.1.2/paper-server/src/main/java/org/bukkit/craftbukkit/util/CraftNBTTagConfigSerializer.java).
- [Projetos Paper](https://docs.papermc.io/paper/dev/project-setup/).
- [Compatibilidade Java/Gradle](https://docs.gradle.org/current/userguide/compatibility.html).

Referências baixadas permanecem em `.research/`, fora da distribuição. Nenhum
fonte baixado é importado ou executado pela implementação.
