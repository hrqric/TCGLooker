using System.Security.Claims;
using TCGLooker.Application.Watchlist;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.API.Watchlist;

internal static class WishlistEndpoints
{
    public static RouteGroupBuilder MapWishlist(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/me/wishlist")
            .RequireAuthorization()
            .WithTags("Wishlist");

        group.MapPost("/", CreateAsync)
            .RequireRateLimiting("wishlist-mutations")
            .WithName("CreateWishlistItem")
            .WithSummary("Cria uma regra de wishlist para o usuário autenticado.");
        group.MapGet("/", ListAsync)
            .WithName("ListWishlist")
            .WithSummary("Lista a wishlist do usuário autenticado.");
        group.MapDelete("/{id:guid}", DisableAsync)
            .RequireRateLimiting("wishlist-mutations")
            .WithName("DisableWishlistItem")
            .WithSummary("Desativa uma regra da wishlist sem apagar seu histórico.");

        return group;
    }

    private static async Task<IResult> CreateAsync(
        WishlistCreateRequest request,
        ClaimsPrincipal principal,
        WishlistService service,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (subject is null)
            return Results.Unauthorized();

        if (!TryParseCondition(request.MinimumCondition, out var condition, out var conditionError))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.MinimumCondition)] = [conditionError!]
            });

        try
        {
            var result = await service.CreateAsync(
                subject,
                new CreateWishlistItem(
                    request.CardId,
                    request.CardPrintingId,
                    request.MaximumPrice?.Amount,
                    request.MaximumPrice?.Currency?.Trim().ToUpperInvariant(),
                    condition),
                cancellationToken);
            return Results.Created($"/api/v1/me/wishlist/{result.Id}", ToResponse(result));
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["wishlist"] = [exception.Message]
            });
        }
        catch (WishlistTargetNotFoundException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Carta não encontrada",
                detail: exception.Message);
        }
        catch (WishlistConflictException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Wishlist já existente",
                detail: exception.Message);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> ListAsync(
        bool? includeInactive,
        ClaimsPrincipal principal,
        WishlistService service,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (subject is null)
            return Results.Unauthorized();
        try
        {
            var result = await service.ListAsync(subject, includeInactive ?? false, cancellationToken);
            return Results.Ok(result.Select(ToResponse));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> DisableAsync(
        Guid id,
        ClaimsPrincipal principal,
        WishlistService service,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (subject is null)
            return Results.Unauthorized();
        try
        {
            return await service.DisableAsync(subject, id, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static object ToResponse(WishlistView item) => new
    {
        item.Id,
        item.CardId,
        item.CardName,
        item.CardPrintingId,
        maximumPrice = item.MaximumPriceAmount is null
            ? null
            : new { amount = item.MaximumPriceAmount, currency = item.MaximumPriceCurrency },
        minimumCondition = item.MinimumCondition is null ? null : ToApi(item.MinimumCondition.Value),
        item.IsActive,
        item.CreatedAt
    };

    private static bool TryParseCondition(
        string? value,
        out CardCondition? condition,
        out string? error)
    {
        condition = value?.Trim().ToLowerInvariant() switch
        {
            null or "" => null,
            "mint" => CardCondition.Mint,
            "near_mint" => CardCondition.NearMint,
            "lightly_played" => CardCondition.LightlyPlayed,
            "moderately_played" => CardCondition.ModeratelyPlayed,
            "heavily_played" => CardCondition.HeavilyPlayed,
            "damaged" => CardCondition.Damaged,
            _ => CardCondition.Unknown
        };
        error = condition == CardCondition.Unknown
            ? "Use mint, near_mint, lightly_played, moderately_played, heavily_played ou damaged."
            : null;
        return error is null;
    }

    private static string ToApi(CardCondition value) => value switch
    {
        CardCondition.Mint => "mint",
        CardCondition.NearMint => "near_mint",
        CardCondition.LightlyPlayed => "lightly_played",
        CardCondition.ModeratelyPlayed => "moderately_played",
        CardCondition.HeavilyPlayed => "heavily_played",
        CardCondition.Damaged => "damaged",
        _ => "unknown"
    };
}

internal sealed record WishlistCreateRequest(
    Guid CardId,
    Guid? CardPrintingId,
    WishlistPriceRequest? MaximumPrice,
    string? MinimumCondition);

internal sealed record WishlistPriceRequest(decimal Amount, string? Currency);

