# IronGate - High-Performance API Gateway
# Multi-stage build for minimal, secure container

# =============================================================================
# STAGE 1: Build
# =============================================================================
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Copy project files first for layer caching
COPY src/IronGate/IronGate.csproj ./src/IronGate/
COPY IronGate.slnx .

# Restore dependencies
RUN dotnet restore IronGate.slnx

# Copy source and build
COPY src/ ./src/
RUN dotnet publish src/IronGate/IronGate.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    --self-contained false

# =============================================================================
# STAGE 2: Runtime (chiseled / distroless)
# =============================================================================
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS runtime
WORKDIR /app

# Create non-root user
RUN addgroup -g 1000 irongate && \
    adduser -u 1000 -G irongate -s /bin/sh -D irongate

# Copy published app
COPY --from=build --chown=irongate:irongate /app/publish .

# Expose ports
EXPOSE 8080

# Health check endpoint
HEALTHCHECK --interval=30s --timeout=3s --start-period=5s --retries=3 \
    CMD wget -q --spider http://localhost:8080/healthz || exit 1

# Run as non-root user
USER irongate

# Entrypoint
ENTRYPOINT ["dotnet", "IronGate.dll"]