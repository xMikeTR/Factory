var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();
builder.Services.AddHttpClient("PricingApi", c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:PricingApi"]!));
var app = builder.Build();
app.MapRazorPages();
app.Run();
