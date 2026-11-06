using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<QuotesDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Quotes")));
builder.Services.AddHttpClient<RatingClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:RatingEngine"]!));
builder.Services.AddSingleton(new ServiceBusClient(builder.Configuration.GetConnectionString("ServiceBus")));
builder.Services.AddSingleton<ReferralPublisher>();
var app = builder.Build();

app.MapPost("/quotes", async (QuoteRequest req, RatingClient rating, QuotesDbContext db, ReferralPublisher referrals) =>
{
    var price = await rating.RateAsync(req);
    var quote = new Quote(req, price);
    db.Quotes.Add(quote);
    await db.SaveChangesAsync();
    if (quote.NeedsReferral) await referrals.SendAsync(quote);
    return Results.Ok(quote);
});
app.Run();
