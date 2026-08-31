using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using TCGLooker.Application.Ingestion;
using TCGLooker.Domain.Common;
using TCGLooker.Domain.Marketplace;

namespace TCGLooker.Infra.Connectors;

internal sealed partial class LigaMagicPageParser
{
    private readonly HtmlParser _parser = new();

    public async Task<IReadOnlyCollection<Uri>> ParseProductLinksAsync(
        string html,
        Uri pageUri,
        CancellationToken cancellationToken)
    {
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);

        return document.QuerySelectorAll("a[href*='view=ecom/item'][href*='refid=']")
            .Select(link => link.GetAttribute("href"))
            .Where(href => !string.IsNullOrWhiteSpace(href))
            .Select(href => new Uri(pageUri, href))
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyCollection<ExternalListing>> ParseProductAsync(
        string html,
        Uri productUri,
        string storeKey,
        CancellationToken cancellationToken)
    {
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);
        if (!IsCardPage(document))
            return [];

        var title = Clean(document.QuerySelector(".nome_pt_cards")?.TextContent)
            ?? Clean(document.QuerySelector(".nome_en_cards")?.TextContent)
            ?? Clean(document.QuerySelector("h1")?.TextContent);
        if (title is null)
            return [];

        var cardName = StripCollectorNumber(title);
        var collectorNumber = ParseCollectorNumber(
            document.QuerySelector(".nome_en_cards")?.TextContent ?? title);
        var referenceId = GetQueryValue(productUri, "refid") ?? productUri.AbsoluteUri;
        var listings = new List<ExternalListing>();

        foreach (var row in document.QuerySelectorAll(".table-cards-row"))
        {
            var priceText = Clean(row.QuerySelector(".card-preco")?.TextContent);
            if (!TryParsePrice(priceText, out var price))
                continue;

            var setLink = row.QuerySelector("a[href*='txt_edicao=']");
            var setCode = setLink is null
                ? null
                : GetQueryValue(new Uri(productUri, setLink.GetAttribute("href")!), "txt_edicao");
            var setName = FirstNonEmpty(
                setLink?.QuerySelector("[title]")?.GetAttribute("title"),
                setLink?.GetAttribute("title"),
                setLink?.TextContent,
                "Coleção desconhecida")!;
            setCode ??= $"name:{TextNormalizer.Normalize(setName)}";

            var language = ParseLanguage(row);
            var condition = ParseCondition(row.QuerySelector(".quality"));
            var extras = Clean(row.QuerySelector(".card-extras")?.TextContent) ?? string.Empty;
            var finish = ParseFinish(extras);
            var variant = ParseVariant(extras);
            var quantity = ParseQuantity(row.TextContent);
            var externalId = CreateExternalId(
                storeKey, referenceId, setCode, language, condition, finish, variant);

            listings.Add(new ExternalListing(
                externalId,
                cardName,
                setCode,
                setName,
                collectorNumber,
                language,
                finish,
                variant,
                title,
                productUri,
                new Money(price, "BRL"),
                condition,
                quantity,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["reference_id"] = referenceId,
                    ["set_code"] = setCode,
                    ["set_name"] = setName,
                    ["language"] = language,
                    ["extras"] = extras
                }));
        }

        return listings;
    }

    public async Task<bool> IsProductPageAsync(string html, CancellationToken cancellationToken)
    {
        var document = await _parser.ParseDocumentAsync(html, cancellationToken);
        return document.QuerySelector(".table-cards-row") is not null;
    }

    private static bool IsCardPage(IDocument document)
    {
        var breadcrumbs = Clean(document.QuerySelector(".breadcrumbs")?.TextContent);
        return breadcrumbs?.Contains("Cards Avulsos", StringComparison.OrdinalIgnoreCase) is true
            || document.QuerySelector(".nome_pt_cards, .nome_en_cards") is not null;
    }

    private static string ParseLanguage(IElement row)
    {
        foreach (var image in row.QuerySelectorAll("img"))
        {
            var value = FirstNonEmpty(image.GetAttribute("alt"), image.GetAttribute("title"));
            if (value?.Contains("Portugu", StringComparison.OrdinalIgnoreCase) is true)
                return "pt-BR";
            if (value?.Contains("Ingl", StringComparison.OrdinalIgnoreCase) is true)
                return "en";
            if (value?.Contains("Japon", StringComparison.OrdinalIgnoreCase) is true)
                return "ja";
            if (value?.Contains("Espan", StringComparison.OrdinalIgnoreCase) is true)
                return "es";
        }

        return "und";
    }

    private static CardCondition ParseCondition(IElement? element)
    {
        var value = FirstNonEmpty(element?.TextContent, element?.GetAttribute("title"))?.ToUpperInvariant();
        if (value is null)
            return CardCondition.Unknown;
        if (value.Contains("NM") || value.Contains("NEAR MINT"))
            return CardCondition.NearMint;
        if (value.Contains("LP") || value.Contains("LIGHTLY"))
            return CardCondition.LightlyPlayed;
        if (value.Contains("MP") || value.Contains("MODERATELY"))
            return CardCondition.ModeratelyPlayed;
        if (value.Contains("HP") || value.Contains("HEAVILY"))
            return CardCondition.HeavilyPlayed;
        if (value.Contains("DMG") || value.Contains("DAMAGED"))
            return CardCondition.Damaged;
        if (value.Contains("MINT"))
            return CardCondition.Mint;
        return CardCondition.Unknown;
    }

    private static CardFinish ParseFinish(string extras)
    {
        if (extras.Contains("reverse", StringComparison.OrdinalIgnoreCase))
            return CardFinish.ReverseHolo;
        if (extras.Contains("foil", StringComparison.OrdinalIgnoreCase)
            || extras.Contains("holo", StringComparison.OrdinalIgnoreCase))
            return CardFinish.Holo;
        return CardFinish.Normal;
    }

    private static string? ParseVariant(string extras)
    {
        var parts = extras.Split([',', '/', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !part.Equals("foil", StringComparison.OrdinalIgnoreCase)
                && !part.Equals("holo", StringComparison.OrdinalIgnoreCase)
                && !part.Equals("reverse holo", StringComparison.OrdinalIgnoreCase))
            .Select(TextNormalizer.Normalize)
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        return parts.Length == 0 ? null : string.Join('+', parts);
    }

    private static int? ParseQuantity(string value)
    {
        var match = QuantityRegex().Match(value);
        return match.Success && int.TryParse(match.Groups[1].Value, out var quantity) ? quantity : null;
    }

    private static bool TryParsePrice(string? value, out decimal price)
    {
        price = 0;
        if (value is null)
            return false;
        var numeric = PriceCleanupRegex().Replace(value, string.Empty);
        return decimal.TryParse(numeric, NumberStyles.Number, CultureInfo.GetCultureInfo("pt-BR"), out price);
    }

    private static string StripCollectorNumber(string value) =>
        Clean(CollectorSuffixRegex().Replace(value, string.Empty)) ?? value;

    private static string? ParseCollectorNumber(string value)
    {
        var match = CollectorNumberRegex().Match(value);
        return match.Success ? Clean(match.Groups[1].Value) : null;
    }

    private static string CreateExternalId(
        string storeKey,
        string referenceId,
        string setCode,
        string language,
        CardCondition condition,
        CardFinish finish,
        string? variant)
    {
        var identity = string.Join('|', storeKey, referenceId, setCode, language,
            condition.ToString(), finish.ToString(), variant ?? string.Empty);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static string? GetQueryValue(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (Uri.UnescapeDataString(parts[0]).Equals(key, StringComparison.OrdinalIgnoreCase))
                return parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : string.Empty;
        }
        return null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.Select(Clean).FirstOrDefault(value => value is not null);

    private static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return WhitespaceRegex().Replace(value, " ").Trim();
    }

    [GeneratedRegex(@"(\d+)\s*unid", RegexOptions.IgnoreCase)]
    private static partial Regex QuantityRegex();

    [GeneratedRegex(@"[^\d,.-]")]
    private static partial Regex PriceCleanupRegex();

    [GeneratedRegex(@"\s*\(\s*#.+?\)\s*$")]
    private static partial Regex CollectorSuffixRegex();

    [GeneratedRegex(@"\(\s*#([^/)]+)")]
    private static partial Regex CollectorNumberRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

internal static class TextNormalizer
{
    public static string Normalize(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                builder.Append(char.ToLowerInvariant(character));
        }

        return Regex.Replace(builder.ToString().Normalize(NormalizationForm.FormC), @"[^a-z0-9]+", " ").Trim();
    }
}
