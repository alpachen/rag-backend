# STEP 1
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# copy everything
COPY . .

# restore
RUN dotnet restore WebAPI/WebAPI.csproj

# publish WebAPI (會自動 build RagDataPipeline)
RUN dotnet publish WebAPI/WebAPI.csproj -c Release -o /app

# STEP 2
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "WebAPI.dll"]

