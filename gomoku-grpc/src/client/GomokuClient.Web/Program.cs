using GomokuClient.Web.Security;
using GomokuClient.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var databasePath = builder.Configuration["Auth:DatabasePath"] ?? "App_Data/auth.db";
var resolvedDatabasePath = Path.GetFullPath(databasePath, builder.Environment.ContentRootPath);
Directory.CreateDirectory(Path.GetDirectoryName(resolvedDatabasePath)!);

var dataProtection = builder.Services.AddDataProtection();
var dataProtectionPath = builder.Configuration["Auth:DataProtectionKeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionPath))
{
    Directory.CreateDirectory(dataProtectionPath);
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath));
}

builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite($"Data Source={resolvedDatabasePath}"));
builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
        options.Lockout.AllowedForNewUsers = true;
    })
    .AddEntityFrameworkStores<AuthDbContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "Gomoku.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.SlidingExpiration = true;
    options.LoginPath = "/Account/Login";
    options.AccessDeniedPath = "/Account/AccessDenied";
});

builder.Services.AddRazorPages();
builder.Services.AddHttpClient();
builder.Services.AddScoped<GrpcGameClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var serverUrl = config["GameServerUrl"] ?? "http://localhost:5224";
    return new GrpcGameClient(serverUrl);
});
builder.Services.AddAntiforgery();

var app = builder.Build();

// 초기 데모는 EnsureCreated를 사용한다. 실제 스키마 변경 전에는 EF migration을 도입한다.
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
    await db.Database.EnsureCreatedAsync();
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.Run();

public partial class Program { }
