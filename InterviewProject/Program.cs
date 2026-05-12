using InterviewProject.Data;
using InterviewProject.Hubs;
using InterviewProject.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.   MVC
builder.Services.AddControllersWithViews();

//註冊 SignalR 服務
builder.Services.AddSignalR();

// 加入 Session
builder.Services.AddSession();

// DB Context
//builder.Services.AddDbContext<AppDbContext>(options =>
//    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
var dbPath = Path.Combine(AppContext.BaseDirectory, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

var app = builder.Build();

// --- 關鍵：自動初始化資料庫 (不寫死所有帳號) ---
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<AppDbContext>();
        // 1. 如果資料庫檔案不存在或表沒蓋好，這行會自動搞定
        context.Database.EnsureCreated();

        // 2. 只在完全沒人時才塞一個 admin，方便你進去操作
        if (!context.Users.Any())
        {
            context.Users.Add(new User { Account = "admin", Password = "123" });
            context.SaveChanges();
            Console.WriteLine("資料庫已初始化，預設管理員 admin 已建立。");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("初始化資料庫失敗: " + ex.Message);
    }
}

// 設定副檔名對照表與靜態檔案 (保持你原本的)
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    ServeUnknownFileTypes = true,
    DefaultContentType = "application/octet-stream"
});

// Configure the HTTP request pipeline.   錯誤處理
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
//app.UseStaticFiles();

app.UseRouting();

// Session 必須放在這裡
app.UseSession();

app.UseAuthorization();

// 路由
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// 加入 SignalR Hub 路由點
// "/chatHub" 是前端 JavaScript 連線時要指定的網址路徑
app.MapHub<ChatHub>("/chatHub");

app.Run();
