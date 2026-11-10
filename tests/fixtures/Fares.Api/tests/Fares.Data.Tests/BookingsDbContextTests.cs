using Microsoft.EntityFrameworkCore;
using Xunit;

public class BookingsDbContextTests
{
    [Fact]
    public async Task Saves_and_reads_a_booking()
    {
        var options = new DbContextOptionsBuilder<BookingsDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var booking = new Booking { Fee = 1200m };
        await using (var db = new BookingsDbContext(options)) { db.Bookings.Add(booking); await db.SaveChangesAsync(); }
        await using (var db = new BookingsDbContext(options)) Assert.Equal(1200m, (await db.Bookings.FindAsync(booking.Id))!.Fee);
    }
}
