// A background job: it has no port, and keeps running until the container stops.
while (true)
{
    Console.WriteLine($"Hello from a .NET console app at {DateTime.UtcNow:O}");
    await Task.Delay(TimeSpan.FromSeconds(5));
}