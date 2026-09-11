using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Threading.RateLimiting;
using TCGLooker.API.Authentication;
using TCGLooker.API.Stores;
using TCGLooker.API.Watchlist;
using TCGLooker.Application.Search;
using TCGLooker.Infra;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddInfrastructure(builder.Configuration);
var developmentAuthentication = builder.Configuration.GetValue<bool>("dev");
if (developmentAuthentication && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "The dev authentication bypass can only be enabled in the Development environment.");
}

if (developmentAuthentication)
{
    builder.Services
        .AddAuthentication(DevelopmentAuthenticationHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevelopmentAuthenticationHandler>(
            DevelopmentAuthenticationHandler.SchemeName,
            _ => { });
}
else
{
    var supabaseAuthority = builder.Configuration["Supabase:Authority"]?.TrimEnd('/');
    if (string.IsNullOrWhiteSpace(supabaseAuthority))
        throw new InvalidOperationException("Supabase:Authority must be configured when dev is false.");

    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = supabaseAuthority;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = supabaseAuthority,
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Supabase:Audience"] ?? "authenticated",
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                NameClaimType = "sub"
            };
        });
}
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("store-preferences", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirstValue("sub") ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddFixedWindowLimiter(
        "site-registration",
        limiter =>
        {
            limiter.PermitLimit = 10;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
    options.AddFixedWindowLimiter(
        "wishlist-mutations",
        limiter =>
        {
            limiter.PermitLimit = 30;
            limiter.Window = TimeSpan.FromMinutes(1);
            limiter.QueueLimit = 0;
            limiter.AutoReplenishment = true;
        });
});

var app = builder.Build();

if (developmentAuthentication)
{
    app.Logger.LogWarning(
        "Development authentication bypass is enabled for local user {DevelopmentUserId}",
        DevelopmentAuthenticationHandler.UserId);
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

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
        ClaimsPrincipal principal,
        ICardSearchRepository repository,
        CancellationToken cancellationToken) =>
    {
        if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            return Results.BadRequest(new { error = "A busca deve ter pelo menos 2 caracteres." });

        var requestedPage = Math.Max(1, page ?? 1);
        var requestedPageSize = Math.Clamp(pageSize ?? 20, 1, 50);
        var result = await repository.SearchAsync(
            q.Trim(), requestedPage, requestedPageSize, principal.FindFirstValue("sub"), cancellationToken);
        return Results.Ok(result);
    })
    .WithName("SearchCards")
    .WithSummary("Busca cartas Pokémon e suas ofertas atualmente disponíveis.");

app.MapStoreRegistration();
app.MapStorePreferences();
app.MapWishlist();

app.Run();

public partial class Program;
