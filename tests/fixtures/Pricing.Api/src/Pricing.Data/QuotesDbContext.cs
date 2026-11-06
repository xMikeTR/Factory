using Microsoft.EntityFrameworkCore;

public class QuotesDbContext(DbContextOptions<QuotesDbContext> options) : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();
}
