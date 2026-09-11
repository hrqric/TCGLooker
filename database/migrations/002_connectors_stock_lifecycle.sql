-- Adds connector execution and conservative stock reconciliation to an existing database.
begin;

alter table tcglooker.listing
    add column if not exists last_seen_run_id uuid,
    add column if not exists consecutive_misses integer not null default 0,
    add column if not exists unavailable_since timestamptz;

alter table tcglooker.scrape_run
    add column if not exists mode text not null default 'incremental';

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'listing_consecutive_misses_check'
          and conrelid = 'tcglooker.listing'::regclass
    ) then
        alter table tcglooker.listing add constraint listing_consecutive_misses_check
            check (consecutive_misses >= 0);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'scrape_run_mode_check'
          and conrelid = 'tcglooker.scrape_run'::regclass
    ) then
        alter table tcglooker.scrape_run add constraint scrape_run_mode_check
            check (mode in ('incremental', 'full'));
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'listing_last_seen_run_id_fkey'
          and conrelid = 'tcglooker.listing'::regclass
    ) then
        alter table tcglooker.listing add constraint listing_last_seen_run_id_fkey
            foreign key (last_seen_run_id) references tcglooker.scrape_run (id);
    end if;
end
$$;

alter table tcglooker.notification_delivery alter column listing_id drop not null;
alter table tcglooker.notification_delivery
    drop constraint if exists notification_delivery_listing_id_fkey;
alter table tcglooker.notification_delivery
    add constraint notification_delivery_listing_id_fkey
    foreign key (listing_id) references tcglooker.listing (id) on delete set null;

drop index if exists tcglooker.ix_listing_card_availability_price;
create index if not exists ix_listing_in_stock_card_price
    on tcglooker.listing (card_printing_id, price_amount)
    where availability = 'in_stock';
create index if not exists ix_listing_store_last_seen_run
    on tcglooker.listing (store_id, last_seen_run_id);
create index if not exists ix_card_set_game on tcglooker.card_set (game_id);
create index if not exists ix_card_game on tcglooker.card (game_id);
create index if not exists ix_card_printing_card on tcglooker.card_printing (card_id);
create index if not exists ix_card_printing_set on tcglooker.card_printing (set_id);
create index if not exists ix_listing_store on tcglooker.listing (store_id);
create index if not exists ix_notification_delivery_listing
    on tcglooker.notification_delivery (listing_id);

commit;
