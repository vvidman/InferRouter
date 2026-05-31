FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project manifests before full source to cache the restore layer
COPY InferRouter.slnx .
COPY src/InferRouter.Core/InferRouter.Core.csproj src/InferRouter.Core/
COPY src/InferRouter.Providers/InferRouter.Providers.csproj src/InferRouter.Providers/
COPY src/InferRouter.Api/InferRouter.Api.csproj src/InferRouter.Api/
RUN dotnet restore src/InferRouter.Api/InferRouter.Api.csproj

COPY src/ src/
RUN dotnet publish src/InferRouter.Api/InferRouter.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

RUN apt-get update && apt-get install -y libgomp1 && rm -rf /var/lib/apt/lists/*

RUN groupadd --system inferrouter && \
    useradd --system --gid inferrouter --no-create-home inferrouter

RUN mkdir -p /var/log/inferrouter /models /run/secrets && \
    chown inferrouter:inferrouter /var/log/inferrouter /models

COPY --from=build --chown=inferrouter:inferrouter /app/publish .

USER inferrouter

ENV LD_LIBRARY_PATH=/app/runtimes/linux-x64/native/avx2
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "InferRouter.Api.dll"]
