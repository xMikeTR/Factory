var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("FaresApi", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:FaresApi"]!));
var app = builder.Build();
app.MapRazorPages();
app.Run();
