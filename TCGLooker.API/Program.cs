using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using TCGLooker.Application.Search;
using TCGLooker.Infra;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.MapGet("/", () => Results.Ok(
    new
    {
        service = "TCGLooker API",
        version = "v1"
    }))
    .ExcludeFromDescription();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.MapGet("/api/v1/cards/search", async (
        string q,
        int? page,
        int? pageSize,
        ICardSearchRepository repository,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Results.BadRequest(new { error = "A busca deve ter pelo menos 2 caracteres." });

        var requestedPage = Math.Max(1, page ?? 1);
        var requestedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
        var result = await repository.SearchAsync(
            q.Trim(), requestedPage, requestedPageSize, cancellationToken);
        return Results.Ok(result);
    })
    .WithName("SearchCards")
    .WithSummary("Busca cartas Pokémon e suas ofertas atualmente disponíveis.");

app.Run();

public partial class Program;
