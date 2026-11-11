using Azure.Messaging.ServiceBus;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<BookingsDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Bookings")));
builder.Services.AddHttpClient<SurchargeClient>(c =>
    c.BaseAddress = new Uri(builder.Configuration["Services:SurchargeEngine"]!));
builder.Services.AddSingleton(new ServiceBusClient(builder.Configuration.GetConnectionString("ServiceBus")));
builder.Services.AddSingleton<EscalationPublisher>();
var app = builder.Build();

app.MapPost("/bookings", async (BookingRequest req, SurchargeClient surcharge, BookingsDbContext db, EscalationPublisher escalations) =>
{
    var price = await surcharge.RateAsync(req);
    var booking = new Booking(req, price);
    db.Bookings.Add(booking);
    await db.SaveChangesAsync();
    if (booking.NeedsEscalation) await escalations.SendAsync(booking);
    return Results.Ok(booking);
});
app.Run();
