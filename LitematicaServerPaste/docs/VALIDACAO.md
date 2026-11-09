# Validação em outro dispositivo

Este roteiro ainda não foi executado. Usar mundo de testes com Paper 26.1.2 e
Litematica 0.27.9. Separar erros de build, carregamento, protocolo e colagem.

## Build e conexão

1. Com JDK 25 e Gradle 9.1.0+ compatível, executar `gradle build`.
2. Confirmar que os testes passam e o jar aparece em `build/libs/`.
3. Instalar somente o LSP como provedor de `servux:litematics` no servidor.
4. Confirmar sua ativação no console. Falha na reflexão deve desativá-lo antes
   de aceitar colagens.
5. Conectar no criativo com `lsp.paste`; ativar `entityDataSync` e
   `pasteUsingServux` no cliente.
6. Após negociar, `/lsp status` deve indicar protocolo **1**.
7. Colar três blocos simples em chunks carregados e conferir a conclusão pelo LSP.

Se o cliente enviar `/setblock` ou `/fill`, ainda está no caminho de comandos.
Conferir a configuração e reconectar antes de testar NBT.

## Inventários e NBT

Salvar o schematic no singleplayer, com máquinas desligadas para separar NBT
de simulação de redstone.

| Caso | Dados a comparar após colar |
| --- | --- |
| Baú simples | Slots 0, 13 e 26, quantidades e nome |
| Baú duplo | Itens distintos em cada metade, todas as orientações |
| Dispenser/dropper | Todos os slots e orientação |
| Hopper/barrel | Slots e componentes de itens |
| Shulker box | Cor, inventário e componentes dos itens |
| Placas | Frente, verso, cores e Unicode |
| Fornalha desligada | Inventário e progresso recebidos |
| Outros blocos com NBT | Dados específicos do tipo de bloco |

Repetir com replace `all` sobre os mesmos estados de blocos e inventários
diferentes no destino: o conteúdo do schematic deve ser restaurado.
Substituir um container não deve despejar itens antigos nem apagar os itens
de uma metade de baú fora da construção.

## Geometria

Conferir rotações de 90/180/270 graus, ambos os espelhamentos, combinações
globais e por sub-região e regiões com dimensões negativas. Incluir escadas
internas/externas, baús duplos, trilhos e blocos direcionais.

Com `rendered_only`, testar camadas X/Y/Z em coordenadas do mundo. Com replace
`none`, blocos existentes devem permanecer. Com `with_non_air`, o ar do
schematic não deve escavar o destino.

## Ciclo de vida e limites

- Sem permissão ou criativo: recusar novas alterações.
- Chunk ausente, altura/borda inválida ou NBT/estado inválido: recusar antes
  de alterar o mundo.
- Cancelar, desconectar, trocar de mundo ou desativar: liberar sessões e
  tickets; alterações já aplicadas permanecem.
- Duas colagens nos mesmos chunks: recusar a segunda antes de alterar.
- Transferência incompleta: encerrar por timeout.
- Construção acima dos limites: recusar com mensagem.
- Colar mob nomeado, armor stand equipado, item frame e veículo com passageiros.
  Repetir com rotação e espelhamento, e conferir dados, orientação e montagem.
- Colar duas vezes e conferir que cada cópia recebe UUIDs próprios. Habilitar
  Ignore Entities numa sub-região e conferir que seus blocos ainda são colados.
- Colar uma região com ticks de bloco e fluido e conferir os atrasos, incluindo
  uma posição ignorada pelo modo de substituição.

Ao relatar falha, incluir versões/build do Paper, Java, Gradle, Litematica,
comando usado, saída do erro e `/lsp status`. Não incluir NBT completo ou dados
privados no diagnóstico.
