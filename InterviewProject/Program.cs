using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using InterviewProject.Data;
using InterviewProject.Services; // 🚀 1. 確保引入 Service 的命名空間

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.   MVC
builder.Services.AddControllersWithViews();

// 🚀 2. 註冊 AI 機器人服務（解決 Unable to resolve service 錯誤）
builder.Services.AddSingleton<JitsiBotService>();

//註冊 SignalR 服務
builder.Services.AddSignalR();

// 加入 Session
builder.Services.AddSession();

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

app.UseRouting();

// Session 必須放在這裡
app.UseSession();

app.UseAuthorization();

// 路由
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();