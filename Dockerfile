FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy the project file first so `dotnet restore` is cached until it changes.
COPY src/DigitalSchoolManagementSystem.Agent.Api/*.csproj src/DigitalSchoolManagementSystem.Agent.Api/
RUN dotnet restore src/DigitalSchoolManagementSystem.Agent.Api/DigitalSchoolManagementSystem.Agent.Api.csproj

COPY src/ src/
RUN dotnet publish src/DigitalSchoolManagementSystem.Agent.Api/DigitalSchoolManagementSystem.Agent.Api.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# settings.json ships in the image; secrets (API keys, Jwt__Key) come from environment variables.
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "DigitalSchoolManagementSystem.Agent.Api.dll"]
