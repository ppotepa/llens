using MixedBasic.Models;

namespace MixedBasic.Services;

public interface IProductService
{
    Product? GetById(int id);
    IEnumerable<Product> GetAll();
    void Add(Product product);
}
