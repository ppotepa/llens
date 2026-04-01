namespace CSharpBasic.Utils;

public static class PriceCalculator
{
    public static decimal ApplyDiscount(decimal price, decimal discountPercent)
    {
        if (discountPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(discountPercent), "Must be between 0 and 100.");

        return price * (1 - discountPercent / 100m);
    }

    public static decimal RoundToNearest(decimal price, decimal step = 0.05m)
        => Math.Round(price / step) * step;
}
