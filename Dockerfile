# Use the official image as a parent image.
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080

# Use the .NET SDK for building our application
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy csproj and restore dependencies
COPY ["restfulAPI/restfulAPI.csproj", "restfulAPI/"]
RUN dotnet restore "restfulAPI/restfulAPI.csproj"

# Copy everything else and build the application
COPY . .
WORKDIR "/src/restfulAPI"
RUN dotnet build "restfulAPI.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "restfulAPI.csproj" -c Release -o /app/publish

# Generate runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "restfulAPI.dll"]

