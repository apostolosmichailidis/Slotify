# Χτίζεται από τη ρίζα του repo: docker build -f src/Slotify.Admin/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Slotify.Core/Slotify.Core.csproj src/Slotify.Core/
COPY src/Slotify.Data/Slotify.Data.csproj src/Slotify.Data/
COPY src/Slotify.Admin/Slotify.Admin.csproj src/Slotify.Admin/
RUN dotnet restore src/Slotify.Admin/Slotify.Admin.csproj

COPY src/ src/
RUN dotnet publish src/Slotify.Admin/Slotify.Admin.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "Slotify.Admin.dll"]
