using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using 面試.Data;
using 面試.Hubs;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.   MVC
builder.Services.AddControllersWithViews();

//註冊 SignalR 服務
builder.Services.AddSignalR();

// 加入 Session
builder.Services.AddSession();

// DB Context
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));
    //options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// 1. 設定副檔名對照表
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".json"] = "application/json";
// 雖然你檔案沒點，但保險起見還是留著
provider.Mappings[".shard1"] = "application/octet-stream"; 

// 2. 套用設定
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    // --- 關鍵：加入下面這行，允許下載沒有副檔名的檔案 ---
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
