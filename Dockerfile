# Listenarr Monorepo Dockerfile
# Builds both backend (.NET API) and frontend (Vue.js) into a single container
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 4545

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["listenarr.api/Listenarr.Api.csproj", "listenarr.api/"]
RUN dotnet restore "listenarr.api/Listenarr.Api.csproj"
COPY . .
WORKDIR "/src/listenarr.api"
# Ensure Node.js is available in the build image so MSBuild targets that run
# the frontend (npm/vite) can execute during `dotnet publish`.
# Use NodeSource to install Node 20 (LTS-compatible for this project).
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& node --version \
	&& npm --version \
	&& apt-get clean \
	&& rm -rf /var/lib/apt/lists/*
RUN dotnet build "Listenarr.Api.csproj" -c Release -o /app/build \
	&& dotnet publish "Listenarr.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
# Install Node.js in the runtime image for Discord bot support
RUN apt-get update \
	&& apt-get install -y --no-install-recommends curl ca-certificates gnupg \
	&& curl -fsSL https://deb.nodesource.com/setup_20.x | bash - \
	&& apt-get install -y --no-install-recommends nodejs \
	&& node --version \
	&& npm --version \
	&& rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "Listenarr.Api.dll"]
