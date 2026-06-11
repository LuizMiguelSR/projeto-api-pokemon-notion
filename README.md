# projeto-api-pokemon-notion

API .NET para sincronizar cartas no Notion com dados da Liga Pokemon.

## Como Funciona

A API consulta o database do Notion e atualiza as cartas usando dados da Liga Pokemon.

- `POST /api/sync/run`: usa a URL ja existente no campo `Liga Pokemon` e atualiza apenas os valores.
- `POST /api/sync/run/search`: processa paginas com `Status = Nao iniciada`, monta a URL da Liga Pokemon usando titulo + `Numero/printedTotal`, grava a URL e atualiza os dados completos.

## Pre-Requisitos

- Integracao do Notion com acesso ao database principal.
- Integracao do Notion com acesso ao database de log, se `NOTION_LOG_DATABASE_ID` for usado.
- Docker e Docker Compose.
- Arquivo `.env` na raiz do projeto.

## Configuracao

Exemplo de `.env`:

```env
NOTION_TOKEN=seu_token_notion
NOTION_DATABASE_ID=seu_database_id
NOTION_LOG_DATABASE_ID=seu_log_database_id
NOTION_LOG_NAME_PROPERTY=Log
NOTION_LOG_DATE_PROPERTY=Data
NOTION_LOG_STATUS_PROPERTY=Status
NOTION_CARD_NAME_PROPERTY=Nome
NOTION_CARD_URL_PROPERTY=Liga Pokemon
NOTION_PRICE_PROPERTY=Valor Normal
NOTION_FOIL_PRICE_PROPERTY=Valor Foil
NOTION_REVERSE_FOIL_PRICE_PROPERTY=Valor Reverse Foil
NOTION_IMAGE_PROPERTY=Imagem
NOTION_TYPE_PROPERTY=Tipo
NOTION_RARITY_PROPERTY=Raridade
NOTION_NUMBER_PROPERTY=Numero
NOTION_PRINTED_TOTAL_PROPERTY=printedTotal
NOTION_STATUS_PROPERTY=Status
NOTION_NOT_STARTED_STATUS_VALUE=Nao iniciada
NOTION_DONE_STATUS_VALUE=Concluido
```

Use os nomes exatamente como estao no seu database do Notion. Se as propriedades usam acento, mantenha o acento no `.env`.

## Rodar Via Docker

```bash
docker compose up -d --build
```

API disponivel em:

```text
http://localhost:8090
```

Verificar status:

```bash
curl -i http://localhost:8090/
```

## Endpoints

### Atualizar Valores Pela URL Existente

```bash
curl -i -X POST http://localhost:8090/api/sync/run
```

Este endpoint le o campo `Liga Pokemon` e atualiza apenas:

- `Valor Normal`
- `Valor Foil`
- `Valor Reverse Foil`

Ele nao atualiza titulo, numero, imagem, tipo, raridade, URL ou status.

### Buscar URL E Atualizar Paginas Nao Iniciadas

```bash
curl -i -X POST http://localhost:8090/api/sync/run/search
```

Este endpoint processa apenas paginas com:

```text
Status = Nao iniciada
```

Para cada pagina, ele usa:

- titulo da pagina
- `Numero`
- `printedTotal`

Com isso monta uma URL da Liga Pokemon no formato:

```text
https://www.ligapokemon.com.br/?view=cards%2Fcard&tipo=1&card=Nome%20(001%2F182)
```

Depois atualiza dados completos e muda o status para `Concluido`.

## Logs

Se `NOTION_LOG_DATABASE_ID` estiver configurado, a API cria registros no database de log com o resultado da sincronizacao.

## Observacoes

- A API pagina a consulta do Notion, entao processa mais de 100 cartas.
- O scraping e feito por parser HTML direto.
- A politica `restart: unless-stopped` esta configurada no Docker Compose para reiniciar os containers apos queda ou reboot, desde que o Docker tambem suba com o sistema.
