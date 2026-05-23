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
RUN chmod -R +x /app/.playwright/node/linux-x64/ 2>/dev/null || true
# -------------------------------------------------------------------

# 執行檔名稱也必須是英文
ENTRYPOINT ["dotnet", "InterviewProject.dll"]