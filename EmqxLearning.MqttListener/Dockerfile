# See https://aka.ms/containerfastmode to understand how Visual Studio uses this
# Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:6.0.28-alpine3.19-arm64v8 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0.420-alpine3.19-arm64v8 AS build
WORKDIR /src
COPY ["EmqxLearning.MqttListener/EmqxLearning.MqttListener.csproj", "EmqxLearning.MqttListener/"]
RUN dotnet restore "EmqxLearning.MqttListener/EmqxLearning.MqttListener.csproj"
COPY . .
WORKDIR "/src/EmqxLearning.MqttListener"
RUN dotnet build "EmqxLearning.MqttListener.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EmqxLearning.MqttListener.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EmqxLearning.MqttListener.dll"]