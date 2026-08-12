FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/TransactionService/TransactionService.csproj ./TransactionService/
RUN dotnet restore ./TransactionService/TransactionService.csproj
COPY src/TransactionService/ ./TransactionService/
RUN dotnet publish ./TransactionService/TransactionService.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" appuser
USER appuser
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5006
EXPOSE 5006
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -f http://localhost:5006/health || exit 1
ENTRYPOINT ["dotnet", "TransactionService.dll"]
