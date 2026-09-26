# Preços via Scrapling

As páginas de cartas são carregadas pelo [Scrapling de D4Vinci](https://github.com/D4Vinci/Scrapling),
usando StealthyFetcher e Chromium. O JavaScript da página é executado e
cards_editions é capturado para o parser existente, que mantém os preços médios
normal, foil e reverse. A busca de sugestões continua usando a API Clube da Liga.

## Desenvolvimento local

Com Python 3.10+ instalado, na raiz do projeto:

```sh
python3 -m venv /tmp/pokemon-scrapling-venv
/tmp/pokemon-scrapling-venv/bin/pip install -r Scraping/requirements.txt
/tmp/pokemon-scrapling-venv/bin/playwright install-deps chromium
/tmp/pokemon-scrapling-venv/bin/patchright install chromium
export LigaPokemon__ScraplingPythonExecutable=/tmp/pokemon-scrapling-venv/bin/python
dotnet run
```

A instalação de dependências do navegador pode exigir privilégios de administrador.
Para instalação permanente, escolha outro diretório para o ambiente virtual.
O Dockerfile instala as dependências automaticamente.

Configuração na seção LigaPokemon (ou variáveis de ambiente com prefixo LigaPokemon__):
- ScraplingPythonExecutable: executável Python com Scrapling instalado (padrão python3).
- ScraplingTimeoutSeconds: limite total por consulta, incluindo inicialização (padrão 120).
- Cookie: cookies opcionais da Liga, enviados por stdin, sem argumentos de linha de comando.

Falhas de navegação, bloqueios e páginas sem preço continuam sendo erros; não são
gravados preços fictícios. A disponibilidade depende da Liga e de suas proteções.
