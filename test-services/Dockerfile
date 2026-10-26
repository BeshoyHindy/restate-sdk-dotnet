# Build from the repository root:
#   docker build . -f test-services/Dockerfile -t restatedev/test-services-dotnet
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish test-services/Restate.Sdk.TestServices.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV PORT=9080
EXPOSE 9080
ENTRYPOINT ["dotnet", "Restate.Sdk.TestServices.dll"]
