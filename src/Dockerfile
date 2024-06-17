FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

COPY Aderis.OpcuaInjection ./Aderis.OpcuaInjection

RUN dotnet restore ./Aderis.OpcuaInjection/Aderis.OpcuaInjection.csproj -r linux-x64

# copy everything else and build app
RUN dotnet publish ./Aderis.OpcuaInjection/Aderis.OpcuaInjection.csproj -r linux-x64 -c Release -o out

FROM mcr.microsoft.com/dotnet/aspnet:8.0.0-jammy-amd64 AS runtime
WORKDIR /app
COPY --from=build /app/out ./

# Remove package lists to reduce image size
RUN rm -rf /var/lib/apt/lists/*

ENTRYPOINT ["dotnet", "Aderis.OpcuaInjection.dll"]

#build example
#docker build --platform linux/amd64 -t OpcuaInjection .