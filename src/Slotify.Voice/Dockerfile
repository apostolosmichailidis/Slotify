# Χτίζεται από τη ρίζα του repo: docker build -f src/Slotify.Voice/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Slotify.Core/Slotify.Core.csproj src/Slotify.Core/
COPY src/Slotify.Data/Slotify.Data.csproj src/Slotify.Data/
COPY src/Slotify.Voice/Slotify.Voice.csproj src/Slotify.Voice/
RUN dotnet restore src/Slotify.Voice/Slotify.Voice.csproj

COPY src/ src/
RUN dotnet publish src/Slotify.Voice/Slotify.Voice.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Slotify.Voice.dll"]
