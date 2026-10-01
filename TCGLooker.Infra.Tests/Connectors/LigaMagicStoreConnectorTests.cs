using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using TCGLooker.Application.Ingestion;
using TCGLooker.Application.Stores;
using TCGLooker.Infra.Connectors;
using Xunit;

namespace TCGLooker.Infra.Tests.Connectors;

public sealed class LigaMagicStoreConnectorTests
{
    [Fact]
    public async Task Refresh_fetches_only_the_known_product_and_returns_its_offers()
    {
        var handler = new FixtureHandler();
        var uri = new Uri("https://example.test/?view=ecom/item&refid=123");
        var offers = await Create(handler).FetchProductAsync(uri, TestContext.Current.CancellationToken);
        Assert.Equal(uri, Assert.Single(handler.Requests));
        Assert.Equal(uri, Assert.Single(offers).Url);
    }

    [Theory]
    [InlineData("https://elsewhere.test/?view=ecom/item&refid=1")]
    [InlineData("https://example.test/?view=ecom/itens&tcg=2&page=1")]
    public async Task Refresh_rejects_unknown_or_external_product_urls(string url)
    {
        var handler = new FixtureHandler();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            Create(handler).FetchProductAsync(new Uri(url), TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Refresh_rejects_a_redirect_to_another_card()
    {
        var handler = new FixtureHandler { RedirectTo = new Uri("https://example.test/?view=ecom/item&refid=other") };
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(handler).FetchProductAsync(
            new Uri("https://example.test/?view=ecom/item&refid=1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Refresh_does_not_treat_a_maintenance_page_as_zero_stock()
    {
        var handler = new FixtureHandler { EmptyProduct = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(handler).FetchProductAsync(
            new Uri("https://example.test/?view=ecom/item&refid=1"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Short_page_with_next_link_is_not_treated_as_end_of_catalog()
    {
        var handler = new FixtureHandler();
        var connector = Create(handler);
        var page = await connector.FetchAsync(new ScrapeRequest(ScrapeMode.Full), TestContext.Current.CancellationToken);
        Assert.Single(page.Listings);
        Assert.Equal(2, page.NextPage);
        Assert.NotNull(page.Fingerprint);
        Assert.Contains("txt_limit=30", handler.Requests[0].Query);
        Assert.DoesNotContain("ultimas=", handler.Requests[0].Query);

        var last = await connector.FetchAsync(new ScrapeRequest(ScrapeMode.Full, 2, page.NextPageUri),
            TestContext.Current.CancellationToken);
        Assert.Equal(page.NextPageUri, handler.Requests[2]);
        Assert.Null(last.NextPage);
        Assert.Single(last.Listings);
        Assert.NotEqual(page.Fingerprint, last.Fingerprint);
    }

    [Fact]
    public async Task Cursor_cannot_send_requests_to_another_origin()
    {
        var handler = new FixtureHandler();
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(handler).FetchAsync(
            new ScrapeRequest(ScrapeMode.Full, 2, new Uri("https://elsewhere.test/?page=2")),
            TestContext.Current.CancellationToken));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Empty_or_unrecognized_catalog_page_does_not_complete_a_cycle()
    {
        var handler = new FixtureHandler { EmptyCatalog = true };
        await Assert.ThrowsAsync<InvalidDataException>(() => Create(handler).FetchAsync(
            new ScrapeRequest(ScrapeMode.Full, 2), TestContext.Current.CancellationToken));
    }

    private static LigaMagicStoreConnector Create(FixtureHandler handler) => new(
        new StoreSource(Guid.NewGuid(), "fake", "Fake", new Uri("https://example.test/"), "fake",
            StoreConnectorTypes.LigaMagic, StoreScope.Global, null),
        new FixtureFactory(handler), new LigaMagicPageParser(), NullLogger<LigaMagicStoreConnector>.Instance);

    private sealed class FixtureFactory(FixtureHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public bool EmptyCatalog { get; init; }
        public bool EmptyProduct { get; init; }
        public Uri? RedirectTo { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var html = uri.Query.Contains("refid=", StringComparison.Ordinal)
                ? """
                    <div class="nome_pt_cards">Charizard</div>
                    <div class="table-cards-row"><span>1 unid.</span><span class="card-preco">R$ 10,00</span></div>
                    """
                : EmptyCatalog ? "<html>Temporary maintenance</html>"
                : uri.Query.Contains("page=2", StringComparison.Ordinal)
                    ? "<a href='/?view=ecom/item&refid=2'>Zubat</a><a href='/?view=ecom/itens&tcg=2&page=1'>1</a>"
                    : "<a href='/?view=ecom/item&refid=1'>Abra</a><a href='/?view=ecom/itens&tcg=2&txt_limit=30&itens_total=31&page=2'>2</a>";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = RedirectTo is null ? request : new HttpRequestMessage(HttpMethod.Get, RedirectTo),
                Content = new StringContent(EmptyProduct ? "<html>Maintenance</html>" : html)
            });
        }
    }
}
