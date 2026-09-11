using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;

namespace TCGLooker.API.Stores;

internal static partial class StoreRegistrationEndpoints
{
    public static RouteHandlerBuilder MapStoreRegistration(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/api/v1/stores", RegisterAsync)
            .RequireAuthorization()
            .RequireRateLimiting("site-registration")
            .WithName("RegisterStore")
            .WithSummary("Valida e cadastra um site global ou exclusivo do usuário autenticado.");

    private static async Task<IResult> RegisterAsync(
        StoreRegistrationRequest request,
        ClaimsPrincipal principal,
        IStoreSiteValidator validator,
        IStoreCatalogRepository repository,
        CancellationToken cancellationToken)
    {
        var subject = principal.FindFirstValue("sub");
        if (!Guid.TryParse(subject, out _))
            return Results.Unauthorized();

        var errors = Validate(request);
        if (errors.Count > 0)
            return Results.ValidationProblem(errors);

        var scope = request.Scope!.Equals("global", StringComparison.OrdinalIgnoreCase)
            ? StoreScope.Global
            : StoreScope.User;
        if (scope == StoreScope.Global && !IsAdministrator(principal))
            return Results.Forbid();

        var baseUrl = new Uri(request.BaseUrl!, UriKind.Absolute);
        var connectorType = request.ConnectorType?.Trim().ToLowerInvariant()
            ?? StoreConnectorTypes.LigaMagic;
        var validation = await validator.ValidateAsync(baseUrl, connectorType, cancellationToken);
        if (!validation.IsValid)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.BaseUrl)] = [validation.Error!]
            });
        }

        try
        {
            var result = await repository.CreateAsync(
                new StoreRegistration(
                    subject,
                    request.Slug!.Trim().ToLowerInvariant(),
                    request.Name!.Trim(),
                    validation.CanonicalBaseUrl!,
                    connectorType,
                    scope),
                cancellationToken);
            return Results.Created($"/api/v1/stores/{result.Id}", new
            {
                result.Id,
                result.Slug,
                result.Name,
                baseUrl = result.BaseUrl.AbsoluteUri,
                result.ConnectorType,
                scope = result.Scope == StoreScope.Global ? "global" : "user"
            });
        }
        catch (StoreConflictException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Site já cadastrado",
                detail: exception.Message);
        }
    }

    private static Dictionary<string, string[]> Validate(StoreRegistrationRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length is < 2 or > 100)
            errors[nameof(request.Name)] = ["O nome deve ter entre 2 e 100 caracteres."];
        if (string.IsNullOrWhiteSpace(request.Slug) || !SlugRegex().IsMatch(request.Slug.Trim()))
            errors[nameof(request.Slug)] = ["O slug deve conter de 2 a 60 caracteres minúsculos, números ou hífens."];
        if (!Uri.TryCreate(request.BaseUrl, UriKind.Absolute, out _))
            errors[nameof(request.BaseUrl)] = ["Informe uma URL absoluta válida."];
        if (string.IsNullOrWhiteSpace(request.Scope)
            || (!request.Scope.Equals("user", StringComparison.OrdinalIgnoreCase)
                && !request.Scope.Equals("global", StringComparison.OrdinalIgnoreCase)))
        {
            errors[nameof(request.Scope)] = ["O escopo deve ser 'user' ou 'global'."];
        }
        if (request.ConnectorType is not null
            && !request.ConnectorType.Equals(StoreConnectorTypes.LigaMagic, StringComparison.OrdinalIgnoreCase))
        {
            errors[nameof(request.ConnectorType)] = ["O único conector disponível é 'liga_magic'."];
        }
        return errors;
    }

    private static bool IsAdministrator(ClaimsPrincipal principal)
    {
        var metadata = principal.FindFirstValue("app_metadata");
        if (string.IsNullOrWhiteSpace(metadata))
            return false;

        try
        {
            using var document = JsonDocument.Parse(metadata);
            return document.RootElement.TryGetProperty("tcglooker_role", out var role)
                && role.ValueKind == JsonValueKind.String
                && role.GetString()?.Equals("admin", StringComparison.OrdinalIgnoreCase) is true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,58}[a-z0-9])?$")]
    private static partial Regex SlugRegex();
}

internal sealed record StoreRegistrationRequest(
    string? Name,
    string? Slug,
    string? BaseUrl,
    string? Scope = "user",
    string? ConnectorType = null);
