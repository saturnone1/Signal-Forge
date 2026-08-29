FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY nuget.config ./nuget.config
COPY packages/ ./packages/
COPY rti/ ./rti/
COPY ASAP.csproj ./
RUN dotnet restore ASAP.csproj --configfile nuget.config

COPY . .
RUN dotnet publish ASAP.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

COPY --from=build /app/publish ./

ENV ASPNETCORE_URLS=http://+:5226
EXPOSE 5226
VOLUME ["/app/data"]

ENTRYPOINT ["dotnet", "ASAP.dll"]
