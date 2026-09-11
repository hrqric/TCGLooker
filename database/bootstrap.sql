-- TCGLooker initial PostgreSQL schema for a Supabase-hosted database.
-- This file has not been applied automatically. Review and run it through the
-- Supabase SQL Editor or convert it into a CLI-generated migration.

begin;

alter database postgres set timezone to 'America/Sao_Paulo';

create schema if not exists extensions;
create extension if not exists pg_trgm with schema extensions;

create schema if not exists tcglooker;
revoke all on schema tcglooker from public;

do $$
begin
    if exists (select 1 from pg_roles where rolname = 'anon') then
        revoke all on schema tcglooker from anon;
    end if;

    if exists (select 1 from pg_roles where rolname = 'authenticated') then
        revoke all on schema tcglooker from authenticated;
    end if;
end
$$;

create table if not exists tcglooker.game
(
    id uuid primary key,
    slug text not null unique,
    name text not null,
    created_at timestamptz not null default now()
);

create table if not exists tcglooker.card_set
(
    id uuid primary key,
    game_id uuid not null references tcglooker.game (id),
    external_code text,
    name text not null,
    released_on date,
    created_at timestamptz not null default now(),
    unique (game_id, external_code)
);

create table if not exists tcglooker.card
(
    id uuid primary key,
    game_id uuid not null references tcglooker.game (id),
    canonical_name text not null,
    normalized_name text not null,
    created_at timestamptz not null default now(),
    unique (game_id, normalized_name)
);

create table if not exists tcglooker.card_printing
(
    id uuid primary key,
    card_id uuid not null references tcglooker.card (id),
    set_id uuid references tcglooker.card_set (id),
    collector_number text,
    language text not null,
    finish text not null default 'unknown'
        check (finish in ('unknown', 'normal', 'holo', 'reverse_holo')),
    variant text,
    created_at timestamptz not null default now(),
    unique nulls not distinct
        (card_id, set_id, collector_number, language, finish, variant)
);

create table if not exists tcglooker.store
(
    id uuid primary key,
    slug text not null unique,
    name text not null,
    base_url text not null,
    connector_key text not null unique,
    connector_type text not null default 'liga_magic',
    scope text not null default 'global' check (scope in ('global', 'user')),
    owner_user_id uuid,
    created_by_user_id uuid,
    is_enabled boolean not null default true,
    created_at timestamptz not null default now(),
    constraint store_scope_owner_check check (
        (scope = 'global' and owner_user_id is null)
        or (scope = 'user' and owner_user_id is not null))
);

create table if not exists tcglooker.listing
(
    id uuid primary key,
    store_id uuid not null references tcglooker.store (id),
    external_id text not null,
    card_printing_id uuid references tcglooker.card_printing (id),
    title text not null,
    normalized_title text not null,
    condition text not null default 'unknown'
        check (condition in
            ('unknown', 'mint', 'near_mint', 'lightly_played',
             'moderately_played', 'heavily_played', 'damaged')),
    price_amount numeric(14, 2) not null check (price_amount >= 0),
    currency char(3) not null,
    quantity integer check (quantity is null or quantity >= 0),
    url text not null,
    fingerprint text not null,
    availability text not null default 'unknown'
        check (availability in ('unknown', 'in_stock', 'out_of_stock')),
    availability_version bigint not null default 1 check (availability_version > 0),
    raw_attributes jsonb not null default '{}'::jsonb,
    first_seen_at timestamptz not null,
    last_seen_at timestamptz not null,
    last_seen_run_id uuid,
    consecutive_misses integer not null default 0 check (consecutive_misses >= 0),
    unavailable_since timestamptz,
    unique (store_id, external_id)
);

create table if not exists tcglooker.scrape_run
(
    id uuid primary key,
    store_id uuid not null references tcglooker.store (id),
    started_at timestamptz not null,
    finished_at timestamptz,
    status text not null
        check (status in ('running', 'succeeded', 'partially_succeeded', 'failed')),
    mode text not null default 'incremental'
        check (mode in ('incremental', 'full')),
    items_seen integer not null default 0 check (items_seen >= 0),
    items_changed integer not null default 0 check (items_changed >= 0),
    error_code text,
    cursor text
);

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'listing_last_seen_run_id_fkey'
          and conrelid = 'tcglooker.listing'::regclass
    ) then
        alter table tcglooker.listing
            add constraint listing_last_seen_run_id_fkey
            foreign key (last_seen_run_id) references tcglooker.scrape_run (id);
    end if;
end
$$;

create table if not exists tcglooker.app_user
(
    id uuid primary key,
    external_auth_id text unique,
    status text not null default 'active' check (status in ('active', 'disabled')),
    created_at timestamptz not null default now()
);

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'store_owner_user_id_fkey'
          and conrelid = 'tcglooker.store'::regclass
    ) then
        alter table tcglooker.store add constraint store_owner_user_id_fkey
            foreign key (owner_user_id) references tcglooker.app_user (id);
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'store_created_by_user_id_fkey'
          and conrelid = 'tcglooker.store'::regclass
    ) then
        alter table tcglooker.store add constraint store_created_by_user_id_fkey
            foreign key (created_by_user_id) references tcglooker.app_user (id) on delete set null;
    end if;
end
$$;

create table if not exists tcglooker.user_store
(
    user_id uuid not null references tcglooker.app_user (id) on delete cascade,
    store_id uuid not null references tcglooker.store (id),
    is_enabled boolean not null default true,
    primary key (user_id, store_id)
);

create table if not exists tcglooker.wishlist_item
(
    id uuid primary key,
    user_id uuid not null references tcglooker.app_user (id) on delete cascade,
    card_id uuid not null references tcglooker.card (id),
    card_printing_id uuid references tcglooker.card_printing (id),
    max_price_amount numeric(14, 2) check (max_price_amount is null or max_price_amount >= 0),
    max_price_currency char(3),
    minimum_condition text check (minimum_condition is null or minimum_condition in
        ('mint', 'near_mint', 'lightly_played', 'moderately_played',
         'heavily_played', 'damaged')),
    is_active boolean not null default true,
    created_at timestamptz not null default now(),
    check ((max_price_amount is null) = (max_price_currency is null))
);

create table if not exists tcglooker.notification_channel
(
    id uuid primary key,
    user_id uuid not null references tcglooker.app_user (id) on delete cascade,
    type text not null check (type in ('telegram', 'whatsapp')),
    destination_ciphertext bytea not null,
    verified_at timestamptz,
    is_enabled boolean not null default true,
    created_at timestamptz not null default now()
);

create table if not exists tcglooker.notification_delivery
(
    id uuid primary key,
    wishlist_item_id uuid not null references tcglooker.wishlist_item (id) on delete cascade,
    listing_id uuid references tcglooker.listing (id) on delete set null,
    channel_id uuid not null references tcglooker.notification_channel (id),
    event_type text not null,
    availability_version bigint not null,
    status text not null check (status in ('pending', 'sent', 'failed')),
    attempts integer not null default 0 check (attempts >= 0),
    next_attempt_at timestamptz,
    sent_at timestamptz,
    created_at timestamptz not null default now(),
    unique (wishlist_item_id, listing_id, channel_id, event_type, availability_version)
);

create table if not exists tcglooker.outbox_message
(
    id uuid primary key,
    type text not null,
    payload jsonb not null,
    occurred_at timestamptz not null,
    processed_at timestamptz,
    attempts integer not null default 0 check (attempts >= 0),
    next_attempt_at timestamptz,
    error_code text,
    idempotency_key text unique
);

create or replace function tcglooker.condition_rank(value text)
returns integer
language sql
immutable
parallel safe
as $$
    select case value
        when 'mint' then 6
        when 'near_mint' then 5
        when 'lightly_played' then 4
        when 'moderately_played' then 3
        when 'heavily_played' then 2
        when 'damaged' then 1
        else 0
    end
$$;

create index if not exists ix_card_normalized_name_trgm
    on tcglooker.card using gin (normalized_name extensions.gin_trgm_ops);

create index if not exists ix_listing_normalized_title_trgm
    on tcglooker.listing using gin (normalized_title extensions.gin_trgm_ops);

create index if not exists ix_listing_in_stock_card_price
    on tcglooker.listing (card_printing_id, price_amount)
    where availability = 'in_stock';

create index if not exists ix_listing_store_last_seen_run
    on tcglooker.listing (store_id, last_seen_run_id);

create index if not exists ix_scrape_run_store_started
    on tcglooker.scrape_run (store_id, started_at desc);

create index if not exists ix_card_set_game on tcglooker.card_set (game_id);
create index if not exists ix_card_game on tcglooker.card (game_id);
create index if not exists ix_card_printing_card on tcglooker.card_printing (card_id);
create index if not exists ix_card_printing_set on tcglooker.card_printing (set_id);
create index if not exists ix_listing_store on tcglooker.listing (store_id);
create unique index if not exists ux_store_base_url_lower
    on tcglooker.store (lower(rtrim(base_url, '/')));
create index if not exists ix_store_enabled_id
    on tcglooker.store (is_enabled, id);
create index if not exists ix_store_owner_user
    on tcglooker.store (owner_user_id) where owner_user_id is not null;
create index if not exists ix_store_created_by_user
    on tcglooker.store (created_by_user_id) where created_by_user_id is not null;
create index if not exists ix_notification_delivery_listing
    on tcglooker.notification_delivery (listing_id);

create index if not exists ix_wishlist_user_active
    on tcglooker.wishlist_item (user_id, is_active);

create unique index if not exists ux_wishlist_active_target
    on tcglooker.wishlist_item
        (user_id, card_id, coalesce(card_printing_id, '00000000-0000-0000-0000-000000000000'::uuid))
    where is_active;

create index if not exists ix_wishlist_active_card_printing
    on tcglooker.wishlist_item (card_id, card_printing_id)
    where is_active;

create index if not exists ix_notification_channel_ready
    on tcglooker.notification_channel (user_id, id)
    where is_enabled and verified_at is not null;

create index if not exists ix_notification_delivery_pending
    on tcglooker.notification_delivery (coalesce(next_attempt_at, created_at), id)
    where status = 'pending';

create index if not exists ix_outbox_pending
    on tcglooker.outbox_message (coalesce(next_attempt_at, occurred_at))
    where processed_at is null;

insert into tcglooker.game (id, slug, name)
values ('f741dc82-cc0f-45e9-b190-8e0e2df38a71', 'pokemon', 'Pokémon')
on conflict (slug) do update set name = excluded.name;

insert into tcglooker.store (id, slug, name, base_url, connector_key, connector_type, scope)
values
    ('d7200682-151b-4d93-9578-74e719d20bd3', 'cardshall', 'Cards Hall',
     'https://www.cardshall.com.br', 'cardshall', 'liga_magic', 'global'),
    ('148d3f56-55b8-4e10-ad31-526741c88aed', 'tabletoptcg', 'Tabletop TCG',
     'https://www.tabletoptcg.com.br', 'tabletoptcg', 'liga_magic', 'global')
on conflict (slug) do update
set name = excluded.name,
    base_url = excluded.base_url,
    connector_key = excluded.connector_key,
    connector_type = excluded.connector_type,
    scope = excluded.scope,
    is_enabled = true;

commit;
