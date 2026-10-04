using GomokuClient.Web.Services;
using Google.Protobuf;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorPages();
builder.Services.AddSingleton<GrpcGameClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var serverUrl = config["GameServerUrl"] ?? "http://localhost:5224";
    return new GrpcGameClient(serverUrl);
});
builder.Services.AddAntiforgery();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.Run();