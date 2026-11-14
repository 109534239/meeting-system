# 基礎環境
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
# 讓 Render 外部可以連進來
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

# 編譯階段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# 這裡必須與您 GitHub 上的英文資料夾名稱一致
COPY ["InterviewProject/InterviewProject.csproj", "InterviewProject/"]
RUN dotnet restore "./InterviewProject/InterviewProject.csproj"
COPY . .
WORKDIR "/src/InterviewProject"
RUN dotnet build "./InterviewProject.csproj" -c $BUILD_CONFIGURATION -o /app/build

# 發佈階段
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./InterviewProject.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# 最終執行階段
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# --- 💡 新增以下這行：切換成 root 帳號來修改 Playwright 的執行檔權限 ---
USER root
# --- AI 面試官用的 Chromium（Playwright）需要的系統函式庫；少了會出現 libglib-2.0.so.0 找不到 ---
RUN apt-get update && apt-get install -y --no-install-recommends \
    libglib2.0-0 libnss3 libnspr4 libatk1.0-0 libatk-bridge2.0-0 libatspi2.0-0 libcups2 \
    libdbus-1-3 libdrm2 libgbm1 libxkbcommon0 libx11-6 libxcb1 libxext6 libxcomposite1 \
    libxdamage1 libxfixes3 libxrandr2 libpango-1.0-0 libcairo2 libasound2 fonts-liberation \
    && rm -rf /var/lib/apt/lists/*
RUN chmod -R +x /app/.playwright/node/linux-x64/ 2>/dev/null || true
# -------------------------------------------------------------------

# 執行檔名稱也必須是英文
ENTRYPOINT ["dotnet", "InterviewProject.dll"]