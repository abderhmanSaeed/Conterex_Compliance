#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:6.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:6.0 AS build
WORKDIR /source
COPY ["src/Conterex.Compliance.Web/Conterex.Compliance.Web.csproj", "src/Conterex.Compliance.Web/"]
COPY ["src/Conterex.Compliance.Presentation/Conterex.Compliance.Presentation.csproj", "src/Conterex.Compliance.Presentation/"]
COPY ["src/Conterex.Compliance.Application/Conterex.Compliance.Application.csproj", "src/Conterex.Compliance.Application/"]
COPY ["src/Conterex.Compliance.Infrastructure/Conterex.Compliance.Infrastructure.csproj", "src/Conterex.Compliance.Infrastructure/"]
COPY ["src/Conterex.Compliance.Domain/Conterex.Compliance.Domain.csproj", "src/Conterex.Compliance.Domain/"]
RUN dotnet restore "src/Conterex.Compliance.Web/Conterex.Compliance.Web.csproj"
COPY . .
WORKDIR "/source/src/Conterex.Compliance.Web"
RUN dotnet build "Conterex.Compliance.Web.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Conterex.Compliance.Web.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Conterex.Compliance.Web.dll"]
