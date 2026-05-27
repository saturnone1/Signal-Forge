# gRPC Workbench — offline (air-gapped) multi-stage build.
#
# Restore uses ONLY the committed ./packages nupkg cache (see nuget.config).
# The final image bundles protoc + grpc_csharp_plugin + well-known-type
# includes under /app/tools so the runtime dynamic proto→C# compilation
# (DynamicProtoCompiler / ProtoLoader) works without a system protoc.
#
# Build:  docker build -t grpc-workbench:test .
# Deploy: kubectl apply -f k8s/local-test-pod.yaml

# ── Build stage ──────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Restore first (offline, from ./packages) for layer caching.
COPY nuget.config ./
COPY packages/ ./packages/
COPY rti/ ./rti/
COPY GrpcWorkbench.csproj ./
RUN dotnet restore GrpcWorkbench.csproj

# Build + publish.
COPY . .
RUN dotnet publish GrpcWorkbench.csproj -c Release -o /app/publish \
        --no-restore /p:UseAppHost=false

# Stage protoc, grpc_csharp_plugin and the well-known-type .proto includes
# from the restored Grpc.Tools package into the publish output's tools/ dir.
# DynamicProtoCompiler/ProtoLoader look here first (AppContext.BaseDirectory/tools).
RUN set -eux; \
    TOOLS_DIR="$(find /root/.nuget/packages/grpc.tools -type d -name linux_x64 | head -n1)"; \
    INCLUDE_DIR="$(find /root/.nuget/packages/grpc.tools -type d -path '*build/native/include' | head -n1)"; \
    test -n "$TOOLS_DIR" && test -n "$INCLUDE_DIR"; \
    mkdir -p /app/publish/tools/include; \
    cp "$TOOLS_DIR/protoc" /app/publish/tools/protoc; \
    cp "$TOOLS_DIR/grpc_csharp_plugin" /app/publish/tools/grpc_csharp_plugin; \
    cp -r "$INCLUDE_DIR/." /app/publish/tools/include/; \
    chmod +x /app/publish/tools/protoc /app/publish/tools/grpc_csharp_plugin; \
    test -f /app/publish/tools/include/google/protobuf/empty.proto

# ── Runtime stage ────────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# protoc / grpc_csharp_plugin are native binaries; aspnet:9.0 (debian) ships
# libstdc++6 + libc already, no extra apt packages required.
COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5226
EXPOSE 5226

ENTRYPOINT ["dotnet", "GrpcWorkbench.dll"]
