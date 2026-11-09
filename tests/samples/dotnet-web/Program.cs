var app = WebApplication.Create(args);
app.MapGet("/", () => "Hello from ASP.NET Core");
app.Run();