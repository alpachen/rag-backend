# =============================
# STEP 1 — Build stage
# =============================
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# 複製 csproj 以利用 docker layer cache（加速 build）
COPY WebAPI/WebAPI.csproj WebAPI/
COPY RagDataPipeline/RagDataPipeline.csproj RagDataPipeline/
COPY Dr.meow/Dr.meow.csproj Dr.meow/


# restore（利用 cache → 減少部署時間）
RUN dotnet restore WebAPI/WebAPI.csproj

# 再複製其他程式碼
COPY . .

# publish（Release 模式，輸出到 /app）
RUN dotnet publish WebAPI/WebAPI.csproj -c Release -o /app


# =============================
# STEP 2 — Runtime stage
# =============================
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app

# Render 的 container 會由外部 port 注入
ENV ASPNETCORE_URLS=http://+:8080  
EXPOSE 8080

# 複製 build 結果
COPY --from=build /app .

# **Render 會在 root 啟動，避免 HTTPS 需求**
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

# 啟動 WebAPI
ENTRYPOINT ["dotnet", "WebAPI.dll"]
