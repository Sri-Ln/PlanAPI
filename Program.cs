using PlanApi;
using Scalar.AspNetCore;
using StackExchange.Redis;
using System.Text.Json.Nodes;

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


app.MapPost("/v1/plan", async (JsonNode body, IPlanRepository repo) =>
{
    var objectId = body["objectId"]?.GetValue<string>();
    if (string.IsNullOrEmpty(objectId))
        return Results.BadRequest(new { error = "objectId is required" });

    if (await repo.ExistsAsync(objectId))
        return Results.Conflict(new { error = $"plan '{objectId}' already exists" });

    await repo.SaveBlobAsync(objectId, body.ToJsonString());
    return Results.Created($"/v1/plan/{objectId}", null);
});

app.UseHttpsRedirection();

app.Run();
