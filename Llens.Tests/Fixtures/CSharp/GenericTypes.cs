using System;
using System.Collections.Generic;

namespace Fixtures.CSharp;

public class Repository<T> where T : class
{
    private readonly List<T> _items = new();

    public void Add(T item)
    {
        _items.Add(item);
    }

    public T? Find(Func<T, bool> predicate)
    {
        return _items.FirstOrDefault(predicate);
    }

    public IEnumerable<T> GetAll()
    {
        return _items;
    }
}

public enum Status
{
    Active,
    Inactive,
    Pending
}
