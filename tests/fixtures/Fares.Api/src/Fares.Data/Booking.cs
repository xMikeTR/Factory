public sealed class Booking
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public decimal Fee { get; set; }
    public string Status { get; set; } = "Draft";
}

public readonly record struct BookingId(Guid Value);
