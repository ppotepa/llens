namespace CSharpBasic.Models;

/// <summary>A product available for purchase.</summary>
public class Product
{
    public int Id { get; init; }
    public required string Name { get; init; }
    public decimal Price { get; init; }
    public int StockQuantity { get; set; }

    public bool IsInStock() => StockQuantity > 0;
}
