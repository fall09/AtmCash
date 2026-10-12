FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY AtmCash.Api/AtmCash.Api.csproj AtmCash.Api/
RUN dotnet restore AtmCash.Api/AtmCash.Api.csproj
COPY AtmCash.Api/ AtmCash.Api/
RUN dotnet publish AtmCash.Api/AtmCash.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "AtmCash.Api.dll"]
