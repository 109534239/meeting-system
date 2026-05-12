# 基礎環境
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app
# 設定 Render 要求的 Port
ENV ASPNETCORE_URLS=http://+:10000
EXPOSE 10000

# 編譯階段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
# 這裡已經換成英文路徑
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
# 執行檔名稱也換成英文了
ENTRYPOINT ["dotnet", "InterviewProject.dll"]