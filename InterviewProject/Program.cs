using Microsoft.EntityFrameworkCore;
using InterviewProject.Data;
using InterviewProject.Hubs;
using InterviewProject.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddSession();

// --- 修正連線字串：確保在 Render (Linux) 環境能讀到正確路徑的 app.db ---
var dbPath = Path.Combine(AppContext.BaseDirectory, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

// --- 關鍵修正：自動建表與初始化 (解決 No such table 問題) ---
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();

        // 核心指令：如果資料庫裡沒有 Users 表，這行會根據 User.cs 自動把表蓋出來
        context.Database.EnsureCreated();

        // 這裡「只」加一個 admin 確保你能登入，其他的帳號你可以進去後動態新增
        if (!context.Users.Any())
        {
            context.Users.Add(new User { Account = "admin", Password = "123" });
            context.SaveChanges();
            Console.WriteLine("資料庫已自動建立 Users 表，並初始化 admin 帳號。");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("初始化資料庫失敗: " + ex.Message);
    }
}

// 靜態檔案設定 (維持你原本的)
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseSession();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Account}/{action=Login}/{id?}");

app.Run();