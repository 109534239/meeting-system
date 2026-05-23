using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using InterviewProject.Data;
using InterviewProject.Services; // 🚀 1. 確保引入 Service 的命名空間

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.   MVC
builder.Services.AddControllersWithViews();

<<<<<<< HEAD
//���U SignalR �A��
builder.Services.AddSignalR();

// �[�J Session
=======
// 🚀 2. 註冊 AI 機器人服務（解決 Unable to resolve service 錯誤）
builder.Services.AddSingleton<JitsiBotService>();

//註冊 SignalR 服務
builder.Services.AddSignalR();

// 加入 Session
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
builder.Services.AddSession();

// DB Context
builder.Services.AddDbContext<AppDbContext>(options =>
     options.UseNpgsql(
<<<<<<< HEAD
        builder.Configuration.GetConnectionString("DefaultConnection"),
        npgsqlOptions => npgsqlOptions.EnableRetryOnFailure(
            maxRetryCount: 5,
            maxRetryDelay: TimeSpan.FromSeconds(10),
            errorCodesToAdd: null
        )));
    //options.UseSqlite("Data Source=app.db"));
    //options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

var app = builder.Build();

// 1. �]�w���ɦW��Ӫ�
var provider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
provider.Mappings[".json"] = "application/json";
// ���M�A�ɮרS�I�A���O�I�_���٬O�d��
provider.Mappings[".shard1"] = "application/octet-stream"; 

// 2. �M�γ]�w
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    // --- ����G�[�J�U���o��A���\�U���S�����ɦW���ɮ� ---
    ServeUnknownFileTypes = true, 
    DefaultContentType = "application/octet-stream"
});

// Configure the HTTP request pipeline.   ���~�B�z
=======
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
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

<<<<<<< HEAD
// Session ������b�o��
=======
// Session 必須放在這裡
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
app.UseSession();

app.UseAuthorization();

<<<<<<< HEAD
// ����
=======
// 路由
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

<<<<<<< HEAD
// �[�J SignalR Hub �����I
// "/chatHub" �O�e�� JavaScript �s�u�ɭn���w�����}���|
app.MapHub<ChatHub>("/chatHub");

app.Run();
=======
app.Run();
>>>>>>> 8866344074956b5162d25ed86764797f0aef079f
