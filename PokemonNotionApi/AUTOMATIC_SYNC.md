# Atualização automática de preços

A aplicação atualiza todas as cartas com URL da Liga Pokémon no Notion ao
iniciar e a cada 24 horas a partir da inicialização. O processamento acontece
em segundo plano, uma carta por vez, e salva os preços no Notion e no histórico.
Cartas sem URL são ignoradas.

Reiniciar ou recriar o contêiner inicia uma nova atualização e reinicia a
contagem das 24 horas. Não é necessário acessar uma página ou fazer login.
O serviço aguarda cada execução terminar antes de iniciar outra; falhas são
registradas e não desativam as próximas execuções. Ao desligar a aplicação,
a execução recebe um pedido de cancelamento.

Configuração opcional, por variáveis de ambiente do contêiner:

- AutomaticSync__Enabled=false: desativa a rotina (padrão true).
- AutomaticSync__IntervalHours=24: intervalo em horas (padrão 24).

Para acompanhar no servidor:

```sh
docker compose logs -f pokemon-notion-api
```

Procure por "automatic card price synchronization". O resultado detalhado
continua sendo registrado pelo fluxo de sincronização existente no Notion.

O agendamento é por instância da aplicação; execute uma única réplica para
evitar que várias réplicas atualizem as mesmas cartas. Atualizações manuais
continuam disponíveis e podem coincidir com a rotina automática.
