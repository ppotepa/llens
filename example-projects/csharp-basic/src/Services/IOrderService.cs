using CSharpBasic.Models;

namespace CSharpBasic.Services;

public interface IOrderService
{
    Order PlaceOrder(string customerName, IEnumerable<(Product product, int quantity)> items);
    Order? GetOrder(int id);
    IEnumerable<Order> GetAllOrders();
    bool CancelOrder(int id);
}
