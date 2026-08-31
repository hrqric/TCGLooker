using TCGLooker.Domain.Marketplace;
using TCGLooker.Infra.Connectors;
using Xunit;

namespace TCGLooker.Infra.Tests.Connectors;

public sealed class LigaMagicPageParserTests
{
    private readonly LigaMagicPageParser _parser = new();

    [Fact]
    public async Task ParseProductLinks_keeps_only_card_details_and_removes_duplicates()
    {
        const string html = """
            <div class="cards">
              <div class="card-item"><div class="card-desc"><div class="title">
                <a href="/?view=ecom/item&amp;refid=123">Charizard</a>
              </div></div></div>
              <div class="card-item"><div class="card-desc"><div class="title">
                <a href="/?view=ecom/item&amp;refid=123">Charizard repetido</a>
              </div></div></div>
              <div class="card-item"><div class="card-desc"><div class="title">
                <a href="/?view=ecom/prod&amp;prod=999">Sleeve</a>
              </div></div></div>
            </div>
            """;

        var links = await _parser.ParseProductLinksAsync(
            html, new Uri("https://example.test/"), CancellationToken.None);

        var link = Assert.Single(links);
        Assert.Equal("https://example.test/?view=ecom/item&refid=123", link.AbsoluteUri);
    }

    [Fact]
    public async Task ParseProduct_reads_all_languages_conditions_finishes_and_stock_states()
    {
        const string html = """
            <div class="breadcrumbs">Home / Pokémon / Cards Avulsos</div>
            <div class="nome_pt_cards">Mega Charizard X ex</div>
            <div class="nome_en_cards">Mega Charizard X ex (#029/∞)</div>
            <div class="table-cards-row">
              <a href="/?view=ecom/itens&amp;txt_edicao=733"><img title="Mega Evolution Promos" /></a>
              <img alt="Português" />
              <span class="quality" title="Near Mint (NM)">NM</span>
              <span class="card-extras">Foil, Promo</span>
              <span>11 unid.</span>
              <span class="card-preco">R$ 29,88</span>
            </div>
            <div class="table-cards-row">
              <a href="/?view=ecom/itens&amp;txt_edicao=733"><img title="Mega Evolution Promos" /></a>
              <img alt="Inglês" />
              <span class="quality">LP</span>
              <span class="card-extras">Reverse Holo</span>
              <span>0 unid.</span>
              <span class="card-preco">R$ 40,00</span>
            </div>
            """;

        var listings = (await _parser.ParseProductAsync(
            html,
            new Uri("https://example.test/?view=ecom/item&refid=456"),
            "store",
            CancellationToken.None)).ToArray();

        Assert.Equal(2, listings.Length);
        Assert.Equal("Mega Charizard X ex", listings[0].CardName);
        Assert.Equal("029", listings[0].CollectorNumber);
        Assert.Equal("733", listings[0].SetExternalCode);
        Assert.Equal("pt-BR", listings[0].Language);
        Assert.Equal(CardCondition.NearMint, listings[0].Condition);
        Assert.Equal(CardFinish.Holo, listings[0].Finish);
        Assert.Equal("promo", listings[0].Variant);
        Assert.Equal(11, listings[0].Quantity);
        Assert.Equal(29.88m, listings[0].Price.Amount);

        Assert.Equal("en", listings[1].Language);
        Assert.Equal(CardCondition.LightlyPlayed, listings[1].Condition);
        Assert.Equal(CardFinish.ReverseHolo, listings[1].Finish);
        Assert.Equal(0, listings[1].Quantity);
        Assert.NotEqual(listings[0].ExternalId, listings[1].ExternalId);

        var changedStock = (await _parser.ParseProductAsync(
            html.Replace("11 unid.", "2 unid.").Replace("R$ 29,88", "R$ 31,50"),
            new Uri("https://example.test/?view=ecom/item&refid=456"),
            "store",
            CancellationToken.None)).First();
        Assert.Equal(listings[0].ExternalId, changedStock.ExternalId);
    }

    [Fact]
    public async Task ParseProduct_ignores_non_card_products()
    {
        const string html = """
            <div class="breadcrumbs">Home / Acessórios</div>
            <h1>Deck Box Charizard</h1>
            <div class="table-cards-row"><span class="card-preco">R$ 99,00</span></div>
            """;

        var listings = await _parser.ParseProductAsync(
            html, new Uri("https://example.test/?prod=99"), "store", CancellationToken.None);

        Assert.Empty(listings);
    }
}
