FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore "PokemonNotionApi.csproj"
RUN dotnet publish "PokemonNotionApi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV PLAYWRIGHT_BROWSERS_PATH=/opt/scrapling-browsers
COPY Scraping/requirements.txt /tmp/scrapling-requirements.txt
RUN apt-get update && apt-get install -y --no-install-recommends python3 python3-venv \
    && python3 -m venv /opt/scrapling \
    && /opt/scrapling/bin/pip install --no-cache-dir -r /tmp/scrapling-requirements.txt \
    && /opt/scrapling/bin/playwright install-deps chromium \
    && /opt/scrapling/bin/patchright install chromium \
    && rm -rf /var/lib/apt/lists/*
ENV LigaPokemon__ScraplingPythonExecutable=/opt/scrapling/bin/python
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8090
EXPOSE 8090
ENTRYPOINT ["dotnet", "PokemonNotionApi.dll"]
