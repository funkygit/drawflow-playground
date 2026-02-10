var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddSignalR();
builder.Services.AddSingleton<DrawflowPlayground.Services.LiteDbContext>();
builder.Services.AddSingleton<DrawflowPlayground.Services.WorkflowExecutionService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DrawflowPlayground.Services.WorkflowExecutionService>());
builder.Services.AddSingleton<DrawflowPlayground.Utilities.IDynamicExecutor, DrawflowPlayground.Utilities.DynamicExecutor>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
}
app.UseRouting();

app.UseAuthorization();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.MapHub<DrawflowPlayground.Hubs.WorkflowHub>("/workflowHub");

app.Run();
