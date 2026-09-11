using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TCGLooker.API.Stores;
using TCGLooker.API.Watchlist;
using TCGLooker.Application.Identity;
using TCGLooker.Application.Stores;
using TCGLooker.Application.Watchlist;
using Xunit;

namespace TCGLooker.Infra.Tests.Api;

public sealed class PreferencesEndpointsTests
{
    [Fact]
    public async Task Stores_allow_public_catalog_but_require_identity_and_explicit_boolean_for_selection()
    {
        await using var host = await TestHost.StartAsync();
        var catalog = await host.Client.GetFromJsonAsync<JsonElement>("/api/v1/stores", cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal("global", catalog[0].GetProperty("scope").GetString());
        Assert.Null(host.Repository.LastUserId);
        var url = $"/api/v1/me/stores/{Guid.NewGuid()}";
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsJsonAsync(url, new { isEnabled = false }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);

        host.Authenticate();
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Client.PutAsJsonAsync(url, new { }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync(url, new { isEnabled = false }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(host.Repository.UserId, host.Repository.LastUserId);
        Assert.False(host.Repository.Selected);
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.PutAsJsonAsync(url, new { isEnabled = true }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.True(host.Repository.Selected);
        host.Repository.StoreExists = false;
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.PutAsJsonAsync(url, new { isEnabled = true }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Wishlist_create_list_delete_uses_internal_owner_and_allows_readding_after_delete()
    {
        await using var host = await TestHost.StartAsync();
        const string url = "/api/v1/me/wishlist/";
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync(url, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PostAsJsonAsync(url, new { cardId = Guid.NewGuid() }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.DeleteAsync(url + Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        host.Authenticate();
        var cardId = Guid.NewGuid();
        var response = await host.Client.PostAsJsonAsync(url, new { cardId, userId = Guid.NewGuid() }, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.Equal(host.Repository.UserId, host.Repository.LastUserId);
        Assert.Single(await host.Client.GetFromJsonAsync<WishlistView[]>(url, cancellationToken: TestContext.Current.CancellationToken) ?? []);
        Assert.Equal(HttpStatusCode.Conflict, (await host.Client.PostAsJsonAsync(url, new { cardId }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);

        host.Client.DefaultRequestHeaders.Remove("X-Test-Subject");
        host.Client.DefaultRequestHeaders.Add("X-Test-Subject", Guid.NewGuid().ToString());
        Assert.Empty(await host.Client.GetFromJsonAsync<WishlistView[]>(url, cancellationToken: TestContext.Current.CancellationToken) ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync(location, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        host.Authenticate();
        Assert.Equal(HttpStatusCode.NoContent, (await host.Client.DeleteAsync(location, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Empty(await host.Client.GetFromJsonAsync<WishlistView[]>(url + "?includeInactive=true", cancellationToken: TestContext.Current.CancellationToken) ?? []);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync(location, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await host.Client.DeleteAsync(url + Guid.Empty, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await host.Client.PostAsJsonAsync(url, new { cardId }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }

    [Fact]
    public async Task Invalid_or_disabled_identity_cannot_manage_preferences()
    {
        await using var host = await TestHost.StartAsync();
        host.Client.DefaultRequestHeaders.Add("X-Test-Subject", "invalid");
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/v1/stores", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        host.Authenticate();
        host.Repository.UserIsActive = false;
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/api/v1/stores", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.PutAsJsonAsync($"/api/v1/me/stores/{Guid.NewGuid()}", new { isEnabled = true }, cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.DeleteAsync($"/api/v1/me/wishlist/{Guid.NewGuid()}", cancellationToken: TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData("{\"cardId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"cardId\":\"11111111-1111-1111-1111-111111111111\",\"minimumCondition\":\"invalid\"}")]
    [InlineData("{\"cardId\":\"11111111-1111-1111-1111-111111111111\",\"maximumPrice\":{\"amount\":-1,\"currency\":\"BRL\"}}")]
    public async Task Wishlist_rejects_invalid_rules(string json)
    {
        await using var host = await TestHost.StartAsync();
        host.Authenticate();
        var response = await host.Client.PostAsync("/api/v1/me/wishlist/", new StringContent(json, System.Text.Encoding.UTF8, "application/json"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, TestRepository repository) : IAsyncDisposable
    {
        public HttpClient Client => client;
        public TestRepository Repository => repository;
        public void Authenticate()
        {
            client.DefaultRequestHeaders.Remove("X-Test-Subject");
            client.DefaultRequestHeaders.Add("X-Test-Subject", repository.Subject);
        }

        public static async Task<TestHost> StartAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var repository = new TestRepository();
            builder.Services.AddSingleton<IUserIdentityRepository>(repository);
            builder.Services.AddSingleton<IStoreCatalogRepository>(repository);
            builder.Services.AddSingleton<IWishlistRepository>(repository);
            builder.Services.AddSingleton<StorePreferencesService>();
            builder.Services.AddSingleton<WishlistService>();
            builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
            builder.Services.AddAuthorization();
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapStorePreferences();
            app.MapWishlist();
            await app.StartAsync(TestContext.Current.CancellationToken);
            return new TestHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()) }, repository);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var subject = Request.Headers["X-Test-Subject"].ToString();
            return Task.FromResult(string.IsNullOrEmpty(subject)
                ? AuthenticateResult.NoResult()
                : AuthenticateResult.Success(new AuthenticationTicket(
                    new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject)], "Test")), "Test")));
        }
    }

    private sealed class TestRepository : IUserIdentityRepository, IStoreCatalogRepository, IWishlistRepository
    {
        public string Subject { get; } = Guid.NewGuid().ToString();
        public Guid UserId { get; } = Guid.NewGuid();
        public Guid? LastUserId { get; private set; }
        public bool UserIsActive { get; set; } = true;
        public bool Selected { get; private set; } = true;
        public bool StoreExists { get; set; } = true;
        private readonly Dictionary<Guid, (Guid Owner, WishlistView Item)> _wishlist = [];

        public Task<AppUserIdentity> GetOrCreateAsync(string externalAuthId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AppUserIdentity(externalAuthId == Subject ? UserId : Guid.Parse(externalAuthId), externalAuthId, UserIsActive));

        public Task<IReadOnlyCollection<StoreView>> ListVisibleAsync(Guid? userId, CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            return Task.FromResult<IReadOnlyCollection<StoreView>>([new(Guid.NewGuid(), "store", "Store", new Uri("https://example.test"), "liga_magic", StoreScope.Global, true, Selected)]);
        }

        public Task<bool> SetSelectionAsync(Guid userId, Guid storeId, bool isEnabled, CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            Selected = isEnabled;
            return Task.FromResult(StoreExists);
        }

        public Task<WishlistView> CreateAsync(Guid userId, CreateWishlistItem item, CancellationToken cancellationToken = default)
        {
            LastUserId = userId;
            if (_wishlist.Values.Any(w => w.Owner == userId && w.Item.CardId == item.CardId && w.Item.CardPrintingId == item.CardPrintingId))
                throw new WishlistConflictException("Duplicado");
            var view = new WishlistView(Guid.NewGuid(), item.CardId, "Pikachu", item.CardPrintingId, item.MaximumPriceAmount, item.MaximumPriceCurrency, item.MinimumCondition, true, DateTimeOffset.UtcNow);
            _wishlist.Add(view.Id, (userId, view));
            return Task.FromResult(view);
        }

        public Task<IReadOnlyCollection<WishlistView>> ListAsync(Guid userId, bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyCollection<WishlistView>>(_wishlist.Values.Where(w => w.Owner == userId).Select(w => w.Item).ToArray());

        public Task<bool> DeleteAsync(Guid userId, Guid wishlistItemId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_wishlist.TryGetValue(wishlistItemId, out var item) && item.Owner == userId && _wishlist.Remove(wishlistItemId));

        public Task<IReadOnlyCollection<StoreSource>> ListEnabledAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StoreRegistrationResult> CreateAsync(StoreRegistration registration, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
