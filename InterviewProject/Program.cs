using Microsoft.EntityFrameworkCore;
using InterviewProject.Data;
using InterviewProject.Hubs;
using InterviewProject.Services;

// 解決 PostgreSQL timestamp with time zone 的 Local / UTC 衝突問題
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// MVC
builder.Services.AddControllersWithViews();

// 註冊 AI 機器人服務
builder.Services.AddSingleton<JitsiBotService>();

// 註冊 SignalR 服務
builder.Services.AddSignalR();

// 加入 Session
builder.Services.AddSession();

// DB Context
builder.Services.AddDbContext<AppDbContext>(options =>
     options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null
        )));

var app = builder.Build();

// 雲端專用：讓 Render 啟動時自動下載 Playwright 瀏覽器核心
if (!app.Environment.IsDevelopment())
{
    Console.WriteLine("---- 正在雲端環境安裝 Playwright 瀏覽器核心... ----");
    Microsoft.Playwright.Program.Main(new string[] { "install", "chromium" });
    Console.WriteLine("---- Playwright 瀏覽器安裝完成！ ----");
}

// 設定副檔名對照表
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".json"] = "application/json";
provider.Mappings[".shard1"] = "application/octet-stream";

// 套用設定
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// 錯誤處理
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();

// Session 必須放在這裡
app.UseSession();

app.UseAuthorization();

// 路由
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// SignalR Hub
app.MapHub<ChatHub>("/chatHub");

app.Run();