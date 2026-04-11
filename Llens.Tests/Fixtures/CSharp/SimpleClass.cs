using System;
using System.Collections.Generic;

namespace Fixtures.CSharp;

public class SimpleClass
{
    public string Name { get; set; } = "";

    public string GetName()
    {
        return Name;
    }

    public int Add(int a, int b)
    {
        return a + b;
    }
}
