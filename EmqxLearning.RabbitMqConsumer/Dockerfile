# See https://aka.ms/containerfastmode to understand how Visual Studio uses this
# Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/runtime:6.0.28-alpine3.19-arm64v8 AS base
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:6.0.420-alpine3.19-arm64v8 AS build
WORKDIR /src
COPY ["EmqxLearning.RabbitMqConsumer/EmqxLearning.RabbitMqConsumer.csproj", "EmqxLearning.RabbitMqConsumer/"]
RUN dotnet restore "EmqxLearning.RabbitMqConsumer/EmqxLearning.RabbitMqConsumer.csproj"
COPY . .
WORKDIR "/src/EmqxLearning.RabbitMqConsumer"
RUN dotnet build "EmqxLearning.RabbitMqConsumer.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "EmqxLearning.RabbitMqConsumer.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "EmqxLearning.RabbitMqConsumer.dll"]