FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The API links shared contracts from the sibling Shared directory, so the
# repository root is intentionally used as the Docker build context.
COPY . .
RUN dotnet restore "ST10443277_PROG7312/SmartX.Api/SmartX.Api.csproj"
RUN dotnet publish "ST10443277_PROG7312/SmartX.Api/SmartX.Api.csproj" \
    --configuration Release \
    --output /app/publish \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

# App_Data is mounted by docker-compose so the file-backed API data persists.
VOLUME ["/app/App_Data"]
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "SmartX.Api.dll"]
