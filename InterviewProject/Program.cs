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

// 🚀 這輪新增：職缺過期後「真的」自動下架 + 觸發自動排程的背景服務
//    （原本只有 HR 手動下架才會觸發，職缺自然過期不會，導致履歷/測驗都審完了卻一直卡著沒有面試房間）
builder.Services.AddHostedService<JobExpiryBackgroundService>();

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
// 🐛 這輪新增：加上連線重試（EnableRetryOnFailure）。
//    現有的 log 已經出現過好幾次「Npgsql.NpgsqlException...遠端主機已強制關閉一個現存的連線」
//    這種 transient（暫時性）連線中斷錯誤——這是雲端 Postgres（尤其免費/入門方案）常見的行為：
//    連線閒置一段時間會被伺服器端主動斷掉，但 EF Core 的連線池不知道，下一個請求剛好撿到
//    一條已經死掉的連線就會直接炸掉整個 request（例如 /Room/Join 這種需要查好幾次資料庫的頁面）。
//    這跟使用哪台電腦、哪種瀏覽器完全無關，純粹是「剛好那個當下連線池裡的連線是不是活的」的機率問題。
//    加上這個設定後，遇到這類已知的暫時性錯誤，EF Core 會自動重試（預設最多 6 次、遞增等待時間），
//    大部分情況使用者根本不會感覺到、頁面照常載入，不會再看到這種整頁報錯的畫面。
builder.Services.AddDbContext<AppDbContext>(options =>
     options.UseNpgsql(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 6,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null)));
//options.UseSqlite("Data Source=app.db"));
//options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// 🐛 這輪新增：這次測試「AI 面試官重複出現」的畫面模式（一開始就重複、整段穩定存在）
//    讓我懷疑是上一場測試殘留的舊分身沒被關掉，一直卡在同一個 Jitsi 房間裡——
//    開發時常見的操作（Ctrl+C、Visual Studio 按停止）不會走到程式裡任何「結束會議」的
//    正常清理流程，Playwright 開的無頭瀏覽器是獨立的作業系統行程，不會因為 .NET 這邊的
//    記憶體歸零就自動關掉，於是留下一個沒人知道存在、但畫面上真實存在的「AI 面試官」分身，
//    下次啟動伺服器、重新測試同一個房間時，新的分身加進去，畫面上就看得到兩個。
//    這裡掛一個「伺服器正常關閉時」的生命週期事件，關閉前把所有還開著的分身都清乾淨。
//    ⚠️ 只能處理「伺服器有機會走到正常關閉流程」的情況；當機、被工作管理員強制結束程序
//    還是會留下孤兒行程，那種情況要自己去工作管理員手動檢查有沒有殘留的
//    chrome.exe / headless_shell.exe 並關掉。
var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
lifetime.ApplicationStopping.Register(() =>
{
    try
    {
        var botService = app.Services.GetRequiredService<JitsiBotService>();
        botService.CloseAllBotsAsync().GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Program] 關閉伺服器時清理 AI 面試官分身失敗：{ex.Message}");
    }
});

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