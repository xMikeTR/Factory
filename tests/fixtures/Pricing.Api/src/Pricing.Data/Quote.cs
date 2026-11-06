public sealed class Quote
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public decimal Premium { get; set; }
    public string Status { get; set; } = "Draft";
}

public readonly record struct QuoteId(Guid Value);
