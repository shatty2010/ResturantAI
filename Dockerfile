FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG PROJECT
WORKDIR /source
COPY docker/certs/ /usr/local/share/ca-certificates/
RUN update-ca-certificates
COPY Directory.Build.props RestaurantAI.sln ./
COPY src/ src/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0
ARG PROJECT
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENV APP_DLL=${PROJECT}.dll
ENV ASPNETCORE_HTTP_PORTS=8080
USER app
EXPOSE 8080
ENTRYPOINT ["sh", "-c", "exec dotnet \"$APP_DLL\""]
