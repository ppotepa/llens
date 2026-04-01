namespace CSharpBasic.Models;

/// <summary>A customer order containing one or more products.</summary>
public class Order
{
    public int Id { get; init; }
    public required string CustomerName { get; init; }
    public List<OrderLine> Lines { get; init; } = [];
    public DateTime PlacedAt { get; init; } = DateTime.UtcNow;

    public decimal Total => Lines.Sum(l => l.LineTotal);
}

public class OrderLine
{
    public required Product Product { get; init; }
    public int Quantity { get; init; }
    public decimal LineTotal => Product.Price * Quantity;
}
