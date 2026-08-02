using InterviewProject.Data;
using InterviewProject.Hubs;
using InterviewProject.Services; // 🚀 1. 確保引入 Service 的命名空間
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// 🌟 關鍵修正：解決 PostgreSQL timestamp with time zone (timestamptz) 的 Local / UTC 衝突問題
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.   MVC
builder.Services.AddControllersWithViews();

// 🚀 2. 註冊 AI 機器人服務（解決 Unable to resolve service 錯誤）
builder.Services.AddSingleton<JitsiBotService>();

// 🚀 JaaS (8x8.vc) JWT 簽發服務
builder.Services.AddSingleton<JaasJwtService>();

// 🚀 Step B：職缺下架後自動判斷履歷結果、自動建立面試房間
builder.Services.AddScoped<AutoInterviewSchedulingService>();

// 🚀 Cloudflare R2 雲端檔案儲存（逐字稿/錄影錄音/AI分析報告），本機與 Render 共用同一個 bucket
builder.Services.AddSingleton<R2StorageService>();

// 🚀 共用的 Gemini API 呼叫服務（ClaudeProxyController 跟 RoomController 都會用到）
builder.Services.AddScoped<GeminiService>();

//註冊 SignalR 服務
builder.Services.AddSignalR();

//✅ 新增：HttpClient（供 ClaudeProxyController 呼叫 Gemini API 用）
builder.Services.AddHttpClient();

// 加入 Session
builder.Services.AddDistributedMemoryCache(); // 💡 新增：Session 需要的記憶體儲存體
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromHours(2);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// DB Context
builder.Services.AddDbContext<AppDbContext>(options =>
     options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection")));
//options.UseSqlite("Data Source=app.db"));
//options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// 🚀 雲端專用：讓 Render 啟動時自動下載 Playwright 瀏覽器核心（解決無核心卡死問題）
if (!app.Environment.IsDevelopment())
{
    Console.WriteLine("---- 正在雲端環境安裝 Playwright 瀏覽器核心... ----");
    Microsoft.Playwright.Program.Main(new string[] { "install", "chromium" });
    Console.WriteLine("---- Playwright 瀏覽器安裝完成！ ----");
}

// 1. 設定副檔名對照表
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".json"] = "application/json";
provider.Mappings[".shard1"] = "application/octet-stream";

// 2. 套用設定
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles(); // 🌟 確保預設的 static files 有被啟用，site.css 才能正確加載

app.UseRouting();

// Session 必須放在這裡
app.UseSession();

app.UseAuthorization();

// 路由
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// ✅ 新增：SignalR Hub 路由
app.MapHub<MeetingHub>("/meetingHub");

app.Run();