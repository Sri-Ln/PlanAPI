using PlanApi;
using Scalar.AspNetCore;
using StackExchange.Redis;

// Top-level statements: this file IS the program (no Main method needed).
var builder = WebApplication.CreateBuilder(args);

// builder.Services is the DI container. Frozen after Build().
builder.Services.AddOpenApi();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured")));

builder.Services.AddSingleton<IPlanRepository, RedisRepository>();

var app = builder.Build();

_ = app.Services.GetRequiredService<IConnectionMultiplexer>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.Run();
