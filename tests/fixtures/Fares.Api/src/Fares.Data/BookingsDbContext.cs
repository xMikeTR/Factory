using Microsoft.EntityFrameworkCore;

public class BookingsDbContext(DbContextOptions<BookingsDbContext> options) : DbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
}
