# projeto-api-pokemon-notion

API .NET para sincronizar cartas Pokémon no Notion com dados da Liga Pokémon, salvar histórico de preços no MySQL e exibir uma página de comparação com gráfico.

## Como Funciona

A API consulta o database principal do Notion, lê as cartas cadastradas e atualiza os preços usando a URL da Liga Pokémon.

- `POST /api/sync/run`: usa a URL já existente no campo `Liga Pokémon`, atualiza os valores e grava o link da página de comparação.
- `POST /api/sync/run/search`: processa páginas com `Status = Não iniciada`, monta a URL da Liga Pokémon usando título + `Número/printedTotal`, grava a URL, atualiza os dados completos e muda o status.
- A cada sincronização com preço encontrado, a API salva um snapshot no MySQL.
- Cada carta recebe um link para a página `/cards/{pageId}/prices`, com tema escuro, imagem da carta, gráfico, filtros por período e tabela histórica.
- Se ainda não houver histórico no MySQL, a página tenta usar os valores atuais do próprio Notion como primeiro ponto do gráfico.

## Pré-Requisitos

- Integração do Notion com acesso ao database principal.
- Integração do Notion com acesso ao database de log, se `NOTION_LOG_DATABASE_ID` for usado.
- Docker e Docker Compose.
- Arquivo `.env` na raiz do projeto.
- Propriedade no Notion para receber o link do gráfico, por exemplo `Grafico de Precos` ou `Gráfico de Preços`.

## Configuração

Exemplo de `.env`:

```env
NOTION_TOKEN=seu_token_notion
NOTION_DATABASE_ID=seu_database_id
NOTION_LOG_DATABASE_ID=seu_log_database_id
NOTION_LOG_NAME_PROPERTY=Log
NOTION_LOG_DATE_PROPERTY=Data
NOTION_LOG_STATUS_PROPERTY=Status

NOTION_CARD_NAME_PROPERTY=Nome
NOTION_CARD_URL_PROPERTY=Liga Pokémon
NOTION_CHART_URL_PROPERTY=Grafico de Precos
NOTION_PRICE_PROPERTY=Valor Normal
NOTION_FOIL_PRICE_PROPERTY=Valor Foil
NOTION_REVERSE_FOIL_PRICE_PROPERTY=Valor Reverse Foil
NOTION_IMAGE_PROPERTY=Imagem
NOTION_TYPE_PROPERTY=Tipo
NOTION_RARITY_PROPERTY=Raridade
NOTION_NUMBER_PROPERTY=Número
NOTION_PRINTED_TOTAL_PROPERTY=printedTotal
NOTION_STATUS_PROPERTY=Status
NOTION_NOT_STARTED_STATUS_VALUE=Não iniciada
NOTION_DONE_STATUS_VALUE=Concluído

APP_PUBLIC_BASE_URL=http://localhost:8090

MYSQL_ROOT_PASSWORD=root
MYSQL_DATABASE=pokemon
MYSQL_USER=pokemon
MYSQL_PASSWORD=pokemon
```

Use os nomes exatamente como estão no seu database do Notion. Se as propriedades usam acento, mantenha o acento no `.env`.

Se você acessa a página por outro computador, não use `localhost` em `APP_PUBLIC_BASE_URL`. Use o nome ou IP do servidor:

```env
APP_PUBLIC_BASE_URL=http://areta:8090
```

## Propriedades Do Notion

A API trabalha melhor com estas propriedades:

- `Nome`: título da carta.
- `Liga Pokémon`: URL da página da carta na Liga Pokémon.
- `Grafico de Precos`: texto ou URL para receber o link da página de comparação.
- `Valor Normal`: preço normal.
- `Valor Foil`: preço foil.
- `Valor Reverse Foil`: preço reverse foil.
- `Imagem`: imagem da carta.
- `Número`: número da carta.
- `printedTotal`: total impresso da coleção.
- `Status`: usado pelo endpoint de busca.

Se `Grafico de Precos` for do tipo `URL`, o link fica clicável de forma mais limpa no Notion. Se for `Texto`, a API também grava o link.

## Rodar Via Docker

Na raiz do projeto:

```bash
docker compose up -d --build
```

API disponível em:

```text
http://localhost:8090
```

Verificar status:

```bash
curl -i http://localhost:8090/
```

O MySQL é iniciado pelo `docker-compose.yml`, e a tabela `card_price_history` é criada automaticamente pela API.

## Endpoints

### Histórico E Gráfico De Preços

```bash
curl -i http://localhost:8090/api/cards/{pageId}/prices
```

Retorna os dados de preço da carta. Primeiro consulta o histórico salvo no MySQL; se não houver histórico, tenta montar um snapshot com os dados atuais do Notion.

```text
http://localhost:8090/cards/{pageId}/prices
```

Abre o front de comparação de preços. A página inclui:

- tema escuro;
- imagem da carta;
- preço normal, foil e reverse foil;
- filtros de `30 dias`, `60 dias`, `90 dias` e `Todo período`;
- gráfico com tooltip ao passar o mouse nos pontos;
- tabela de dados históricos;
- botão para abrir a carta na Liga Pokémon.

### Atualizar Valores Pela URL Existente

```bash
curl -i -X POST http://localhost:8090/api/sync/run
```

Este endpoint lê o campo `Liga Pokémon` e atualiza:

- `Valor Normal`;
- `Valor Foil`;
- `Valor Reverse Foil`;
- `Grafico de Precos`, com o link da página de comparação.

Quando consegue extrair preços, também grava um snapshot no MySQL.

Este endpoint não altera título, número, imagem, tipo, raridade, URL da Liga Pokémon ou status.

### Buscar URL E Atualizar Páginas Não Iniciadas

```bash
curl -i -X POST http://localhost:8090/api/sync/run/search
```

Este endpoint processa apenas páginas com:

```text
Status = Não iniciada
```

Para cada página, ele usa:

- título da página;
- `Número`;
- `printedTotal`.

Com isso, monta uma URL da Liga Pokémon no formato:

```text
https://www.ligapokemon.com.br/?view=cards%2Fcard&tipo=1&card=Nome%20(001%2F182)
```

Depois atualiza dados completos, grava o link do gráfico e muda o status para `Concluído`.

## Histórico De Preços

Os snapshots são salvos na tabela `card_price_history`, com:

- ID da página do Notion;
- nome da carta;
- número da carta;
- URL da Liga Pokémon;
- URL da imagem;
- preço normal;
- preço foil;
- preço reverse foil;
- data e hora da captura.

O gráfico fica mais útil conforme novas sincronizações criam novos pontos ao longo do tempo.

## Logs

Se `NOTION_LOG_DATABASE_ID` estiver configurado, a API cria registros no database de log com o resultado da sincronização.

## Observações

- A API faz a paginação da consulta do Notion, então processa mais de 100 cartas.
- O scraping é feito por parser HTML direto.
- A política `restart: unless-stopped` está configurada no Docker Compose para reiniciar os containers após queda ou reboot, desde que o Docker também suba com o sistema.
- O ícone padrão usa a Poké Ball do Wikimedia Commons. Se existir um arquivo `PokemonNotionApi/wwwroot/pokemon-tracker.gif`, a página usa esse GIF como símbolo do tracker.
