using System;
using System.Collections.Generic;
using Fixtures.CSharp;

namespace Fixtures.CSharp;

public class MethodCalls
{
    public void RunAll()
    {
        var obj = new SimpleClass();
        var name = obj.GetName();
        var sum = obj.Add(1, 2);

        var circle = new Circle();
        circle.Radius = 5.0;
        var area = circle.Area();
        var desc = circle.Describe();

        var rect = new Rectangle();
        var rectArea = rect.Area();
    }
}
