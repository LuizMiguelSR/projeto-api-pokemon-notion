# projeto-api-pokemon-notion

API .NET para sincronizar cartas no Notion com dados da Liga Pokémon (foto, preço e informações da carta).

## Como funciona

1. A API consulta sua base do Notion.
2. Para cada página, lê o campo com URL da carta na Liga Pokémon.
3. Faz scrape da página da carta (ex.: Boltund 009/023).
4. Atualiza no Notion: nome, número, preço, imagem, tipo, raridade e status.

## Pré-requisitos

- Integração do Notion com acesso ao database.
- `NOTION_TOKEN` e `NOTION_DATABASE_ID`.
- Docker Desktop (para rodar em container).

## Rodar via Docker

Crie um arquivo `.env` na raiz:

```env
NOTION_TOKEN=seu_token_notion
NOTION_DATABASE_ID=seu_database_id
NOTION_CARD_NAME_PROPERTY=Name
NOTION_CARD_URL_PROPERTY=Link
NOTION_PRICE_PROPERTY=Preço
NOTION_IMAGE_PROPERTY=Imagem
NOTION_TYPE_PROPERTY=Tipo
NOTION_RARITY_PROPERTY=Raridade
NOTION_NUMBER_PROPERTY=Número
NOTION_STATUS_PROPERTY=Status
NOTION_DONE_STATUS_VALUE=Concluído
```

Suba os containers:

```bash
docker compose up --build
```

API disponível em: `http://localhost:8080`

## Endpoints

- `POST /api/sync/run`: sincroniza todas as páginas do database.
- `POST /api/sync/page/{pageId}`: sincroniza uma página específica.

## Observações

- O campo `Link` (ou o nome que você configurar) precisa conter a URL da carta da Liga Pokémon.
- O scraping está implementado por parser HTML direto (sem depender de IA). Se quiser, posso adicionar uma etapa opcional com API de IA para extração assistida quando o HTML mudar.
