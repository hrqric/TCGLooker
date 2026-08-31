using Npgsql;
using TCGLooker.Application.Search;
using TCGLooker.Infra.Connectors;

namespace TCGLooker.Infra.Postgres;

internal sealed class PostgresCardSearchRepository(PostgresConnectionFactory connectionFactory)
    : ICardSearchRepository
{
    public async Task<CardSearchPage> SearchAsync(
        string query,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var normalizedQuery = TextNormalizer.Normalize(query);
        await using var connection = await connectionFactory.OpenConnectionAsync(cancellationToken);

        await using var countCommand = new NpgsqlCommand("""
            select count(*)
            from tcglooker.card c
            where (c.normalized_name OPERATOR(extensions.%) @query
                   or c.normalized_name like '%' || @query || '%')
              and exists (
                  select 1
                  from tcglooker.card_printing cp
                  join tcglooker.listing l on l.card_printing_id = cp.id
                  where cp.card_id = c.id and l.availability = 'in_stock')
            """, connection);
        countCommand.Parameters.AddWithValue("query", normalizedQuery);
        var total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken))!;

        await using var command = new NpgsqlCommand("""
            with matched_cards as (
                select c.id, c.canonical_name,
                       extensions.similarity(c.normalized_name, @query) as rank
                from tcglooker.card c
                where (c.normalized_name OPERATOR(extensions.%) @query
                       or c.normalized_name like '%' || @query || '%')
                  and exists (
                      select 1
                      from tcglooker.card_printing cp
                      join tcglooker.listing l on l.card_printing_id = cp.id
                      where cp.card_id = c.id and l.availability = 'in_stock')
                order by rank desc, c.canonical_name, c.id
                limit @page_size offset @offset
            )
            select mc.id, mc.canonical_name,
                   l.id, s.name, cs.name, cp.collector_number,
                   cp.language, cp.finish, cp.variant, l.condition,
                   l.price_amount, l.currency, l.quantity,
                   l.availability, l.url, l.last_seen_at
            from matched_cards mc
            join tcglooker.card_printing cp on cp.card_id = mc.id
            left join tcglooker.card_set cs on cs.id = cp.set_id
            join tcglooker.listing l on l.card_printing_id = cp.id
            join tcglooker.store s on s.id = l.store_id
            where l.availability = 'in_stock'
            order by mc.rank desc, mc.canonical_name, l.price_amount, l.id
            """, connection);
        command.Parameters.AddWithValue("query", normalizedQuery);
        command.Parameters.AddWithValue("page_size", pageSize);
        command.Parameters.AddWithValue("offset", (page - 1) * pageSize);

        var results = new List<CardSearchResult>();
        var offersByCard = new Dictionary<Guid, List<ListingSummary>>();
        var names = new Dictionary<Guid, string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var cardId = reader.GetGuid(0);
            names[cardId] = reader.GetString(1);
            if (!offersByCard.TryGetValue(cardId, out var offers))
            {
                offers = [];
                offersByCard[cardId] = offers;
            }

            offers.Add(new ListingSummary(
                reader.GetGuid(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? "Coleção desconhecida" : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetString(9),
                reader.GetDecimal(10),
                reader.GetString(11).Trim(),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.GetString(13),
                new Uri(reader.GetString(14)),
                reader.GetFieldValue<DateTimeOffset>(15)));
        }

        foreach (var (cardId, offers) in offersByCard)
            results.Add(new CardSearchResult(cardId, names[cardId], offers));

        return new CardSearchPage(results, page, pageSize, total);
    }
}
