using PlanApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Scalar.AspNetCore;
using StackExchange.Redis;
using System.Text.Json.Nodes;
using Json.Schema;
using System.Linq;
using System.Text.Json;

// Top-level statements: this file IS the program (no Main method needed).
var builder = WebApplication.CreateBuilder(args);

// builder.Services is the DI container. Frozen after Build().
builder.Services.AddOpenApi();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(
        builder.Configuration.GetConnectionString("Redis")
        ?? throw new InvalidOperationException("ConnectionStrings:Redis is not configured")));

builder.Services.AddSingleton(JsonSchema.FromFile(
    Path.Combine(builder.Environment.ContentRootPath, "schema.json")));

builder.Services.AddSingleton<IPlanRepository, RedisRepository>();

// Resource-server auth: validate Google-issued ID tokens, never mint them.
// Authority triggers OIDC discovery + JWKS fetch, so signing keys auto-rotate.
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = "https://accounts.google.com";
        options.Audience = builder.Configuration["Google:ClientId"]
            ?? throw new InvalidOperationException("Google:ClientId is not configured");
    });
builder.Services.AddAuthorization();

var app = builder.Build();

_ = app.Services.GetRequiredService<IConnectionMultiplexer>();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseAuthentication();
app.UseAuthorization();

// All plan endpoints require a valid Bearer token; unauthenticated -> 401.
var plan = app.MapGroup("/v1/plan").RequireAuthorization();

plan.MapPost("", async (JsonNode body, IPlanRepository repo, JsonSchema schema, HttpResponse response) =>
{
    var result = schema.Evaluate(body.Deserialize<JsonElement>(), new EvaluationOptions { OutputFormat = OutputFormat.List });
    if (!result.IsValid)
    {
        var errors = result.Details
            .Where(d => d.Errors is not null && d.Errors.Count > 0)
            .SelectMany(d => d.Errors!.Select(e => new
            {
                path = d.InstanceLocation.ToString(),
                keyword = e.Key,
                message = e.Value
            }))
            .ToList();
        return Results.BadRequest(new { errors });
    }

    var objectId = body["objectId"]!.GetValue<string>();

    if (await repo.ExistsAsync(objectId))
        return Results.Conflict(new { error = $"plan '{objectId}' already exists" });

    await repo.SaveFlattenedAsync(PlanFlattener.Decompose(body));
    response.Headers.ETag = ETag.Compute(body);
    return Results.Created($"/v1/plan/{objectId}", null);
});

plan.MapGet("/{objectId}", async (string objectId, IPlanRepository repo, HttpRequest request, HttpResponse response) =>
{
    var plan = await repo.GetAsync(objectId);
    if (plan is null) return Results.NotFound();

    var etag = ETag.Compute(plan);
    response.Headers.ETag = etag;

    if (request.Headers.IfNoneMatch.Contains(etag))
        return Results.StatusCode(StatusCodes.Status304NotModified);

    return Results.Ok(plan);
});

plan.MapDelete("/{objectId}", async (string objectId, IPlanRepository repo) =>
{
    var deleted = await repo.DeleteAsync(objectId);
    return deleted ? Results.NoContent() : Results.NotFound();
});


app.UseHttpsRedirection();

app.Run();
