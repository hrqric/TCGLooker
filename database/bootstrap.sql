-- TCGLooker initial PostgreSQL schema for a Supabase-hosted database.
-- This file has not been applied automatically. Review and run it through the
-- Supabase SQL Editor or convert it into a CLI-generated migration.

begin;

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
    is_enabled boolean not null default true,
    created_at timestamptz not null default now()
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
    items_seen integer not null default 0 check (items_seen >= 0),
    items_changed integer not null default 0 check (items_changed >= 0),
    error_code text,
    cursor text
);

create table if not exists tcglooker.app_user
(
    id uuid primary key,
    external_auth_id text unique,
    status text not null default 'active' check (status in ('active', 'disabled')),
    created_at timestamptz not null default now()
);

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
    minimum_condition text,
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
    listing_id uuid not null references tcglooker.listing (id),
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
    error_code text
);

create index if not exists ix_card_normalized_name_trgm
    on tcglooker.card using gin (normalized_name extensions.gin_trgm_ops);

create index if not exists ix_listing_normalized_title_trgm
    on tcglooker.listing using gin (normalized_title extensions.gin_trgm_ops);

create index if not exists ix_listing_card_availability_price
    on tcglooker.listing (card_printing_id, availability, price_amount);

create index if not exists ix_scrape_run_store_started
    on tcglooker.scrape_run (store_id, started_at desc);

create index if not exists ix_wishlist_user_active
    on tcglooker.wishlist_item (user_id, is_active);

create index if not exists ix_outbox_pending
    on tcglooker.outbox_message (coalesce(next_attempt_at, occurred_at))
    where processed_at is null;

insert into tcglooker.game (id, slug, name)
values ('f741dc82-cc0f-45e9-b190-8e0e2df38a71', 'pokemon', 'Pokémon')
on conflict (slug) do update set name = excluded.name;

insert into tcglooker.store (id, slug, name, base_url, connector_key)
values
    ('d7200682-151b-4d93-9578-74e719d20bd3', 'cardshall', 'Cards Hall',
     'https://www.cardshall.com.br', 'cardshall'),
    ('148d3f56-55b8-4e10-ad31-526741c88aed', 'tabletoptcg', 'Tabletop TCG',
     'https://www.tabletoptcg.com.br', 'tabletoptcg')
on conflict (slug) do update
set name = excluded.name,
    base_url = excluded.base_url,
    connector_key = excluded.connector_key,
    is_enabled = true;

commit;
