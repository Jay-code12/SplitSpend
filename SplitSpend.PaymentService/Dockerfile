FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/PaymentService/PaymentService.csproj ./PaymentService/
RUN dotnet restore ./PaymentService/PaymentService.csproj
COPY src/PaymentService/ ./PaymentService/
RUN dotnet publish ./PaymentService/PaymentService.csproj \
    -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
RUN adduser --disabled-password --gecos "" appuser
USER appuser
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5007
EXPOSE 5007
HEALTHCHECK --interval=10s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -f http://localhost:5007/health || exit 1
ENTRYPOINT ["dotnet", "PaymentService.dll"]
