using Scalar.AspNetCore;

// Top-level statements: this file IS the program (no Main method needed).
var builder = WebApplication.CreateBuilder(args);

// builder.Services is the DI container. Frozen after Build().
builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseHttpsRedirection();

app.Run();
