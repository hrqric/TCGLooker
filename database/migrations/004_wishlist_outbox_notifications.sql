begin;

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

alter table tcglooker.outbox_message
    add column if not exists idempotency_key text;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'outbox_message_idempotency_key_key'
          and conrelid = 'tcglooker.outbox_message'::regclass
    ) then
        alter table tcglooker.outbox_message
            add constraint outbox_message_idempotency_key_key unique (idempotency_key);
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'wishlist_item_minimum_condition_check'
          and conrelid = 'tcglooker.wishlist_item'::regclass
    ) then
        alter table tcglooker.wishlist_item
            add constraint wishlist_item_minimum_condition_check
            check (minimum_condition is null or minimum_condition in
                ('mint', 'near_mint', 'lightly_played', 'moderately_played',
                 'heavily_played', 'damaged'));
    end if;
end
$$;

with duplicated as (
    select id,
           row_number() over (
               partition by user_id, card_id, card_printing_id
               order by created_at desc, id) as position
    from tcglooker.wishlist_item
    where is_active
)
update tcglooker.wishlist_item item
set is_active = false
from duplicated
where item.id = duplicated.id and duplicated.position > 1;

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

commit;

