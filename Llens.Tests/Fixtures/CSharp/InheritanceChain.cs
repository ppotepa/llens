using System;

namespace Fixtures.CSharp;

public interface IShape
{
    double Area();
    string Describe();
}

public class Circle : IShape
{
    public double Radius { get; set; }

    public double Area()
    {
        return Math.PI * Radius * Radius;
    }

    public string Describe()
    {
        return $"Circle with radius {Radius}";
    }
}

public class Rectangle : IShape
{
    public double Width { get; set; }
    public double Height { get; set; }

    public double Area()
    {
        return Width * Height;
    }

    public string Describe()
    {
        return $"Rectangle {Width}x{Height}";
    }
}
