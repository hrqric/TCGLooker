using System.Security.Claims;
using TCGLooker.Application.Stores;

namespace TCGLooker.API.Stores;

internal static class StorePreferencesEndpoints
{
    public static void MapStorePreferences(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/stores", ListAsync)
            .WithTags("Stores")
            .WithName("ListStores")
            .WithSummary("Lista lojas globais e, com autenticação, as lojas privadas e preferências do usuário.");

        endpoints.MapPut("/api/v1/me/stores/{storeId:guid}", SetSelectionAsync)
            .RequireAuthorization()
            .RequireRateLimiting("store-preferences")
            .WithTags("Stores")
            .WithName("SetStoreSelection")
            .WithSummary("Habilita ou desabilita uma fonte somente para o usuário autenticado.");
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        StorePreferencesService service,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (principal.Identity?.IsAuthenticated == true && subject is null)
            return Results.Unauthorized();
        try
        {
            var stores = await service.ListAsync(subject, cancellationToken);
            return Results.Ok(stores.Select(store => new
            {
                store.Id,
                store.Slug,
                store.Name,
                baseUrl = store.BaseUrl.AbsoluteUri,
                store.ConnectorType,
                scope = store.Scope == StoreScope.Global ? "global" : "user",
                store.IsEnabled,
                store.IsSelected
            }));
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }

    private static async Task<IResult> SetSelectionAsync(
        Guid storeId,
        StoreSelectionRequest request,
        ClaimsPrincipal principal,
        StorePreferencesService service,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (subject is null)
            return Results.Unauthorized();
        if (request.IsEnabled is null)
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["isEnabled"] = ["Informe true para habilitar ou false para desabilitar a fonte."]
            });
        try
        {
            return await service.SetSelectionAsync(subject, storeId, request.IsEnabled.Value, cancellationToken)
                ? Results.NoContent()
                : Results.NotFound();
        }
        catch (UnauthorizedAccessException)
        {
            return Results.Unauthorized();
        }
    }
}

internal sealed record StoreSelectionRequest(bool? IsEnabled);
