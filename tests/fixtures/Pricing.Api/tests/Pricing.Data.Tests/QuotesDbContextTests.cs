using Microsoft.EntityFrameworkCore;
using Xunit;

public class QuotesDbContextTests
{
    [Fact]
    public async Task Saves_and_reads_a_quote()
    {
        var options = new DbContextOptionsBuilder<QuotesDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var quote = new Quote { Premium = 1200m };
        await using (var db = new QuotesDbContext(options)) { db.Quotes.Add(quote); await db.SaveChangesAsync(); }
        await using (var db = new QuotesDbContext(options)) Assert.Equal(1200m, (await db.Quotes.FindAsync(quote.Id))!.Premium);
    }
}
