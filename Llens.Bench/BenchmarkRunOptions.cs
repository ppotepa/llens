namespace Llens.Bench;

public enum BenchmarkTemperature
{
    Warm,
    Cold
}

public sealed record BenchmarkRunOptions(
    BenchmarkTemperature Temperature = BenchmarkTemperature.Warm,
    int RepeatIndex = 1,
    int RepeatCount = 1)
{
    public bool UseWarmCaches => Temperature == BenchmarkTemperature.Warm;
    public string TemperatureLabel => Temperature.ToString().ToLowerInvariant();
}
