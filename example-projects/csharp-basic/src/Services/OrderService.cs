using CSharpBasic.Models;
using CSharpBasic.Utils;

namespace CSharpBasic.Services;

public class OrderService : IOrderService
{
    private readonly Dictionary<int, Order> _store = [];
    private int _nextId = 1;

    public Order PlaceOrder(string customerName, IEnumerable<(Product product, int quantity)> items)
    {
        var lines = items.Select(i => new OrderLine { Product = i.product, Quantity = i.quantity }).ToList();
        var order = new Order { Id = _nextId++, CustomerName = customerName, Lines = lines };
        _store[order.Id] = order;
        return order;
    }

    public Order? GetOrder(int id) => _store.GetValueOrDefault(id);

    public IEnumerable<Order> GetAllOrders() => _store.Values;

    public bool CancelOrder(int id) => _store.Remove(id);

    public decimal GetDiscountedTotal(int id, decimal discountPercent)
    {
        var order = GetOrder(id) ?? throw new KeyNotFoundException($"Order {id} not found.");
        return PriceCalculator.ApplyDiscount(order.Total, discountPercent);
    }
}
