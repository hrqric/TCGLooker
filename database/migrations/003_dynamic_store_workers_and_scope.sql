-- Adds dynamic connector configuration and global/user-owned store visibility.
-- The Supabase CLI is not installed in this workspace, so this repository-style
-- migration was created manually and must be reviewed/applied before deployment.
begin;

alter table tcglooker.store
    add column if not exists connector_type text not null default 'liga_magic',
    add column if not exists scope text not null default 'global',
    add column if not exists owner_user_id uuid,
    add column if not exists created_by_user_id uuid;

do $$
begin
    if not exists (
        select 1 from pg_constraint
        where conname = 'store_scope_check'
          and conrelid = 'tcglooker.store'::regclass
    ) then
        alter table tcglooker.store add constraint store_scope_check
            check (scope in ('global', 'user'));
    end if;

    if not exists (
        select 1 from pg_constraint
        where conname = 'store_scope_owner_check'
          and conrelid = 'tcglooker.store'::regclass
    ) then
        alter table tcglooker.store add constraint store_scope_owner_check
            check (
                (scope = 'global' and owner_user_id is null)
                or (scope = 'user' and owner_user_id is not null));
    end if;

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

update tcglooker.store
set connector_type = 'liga_magic', scope = 'global'
where connector_type is null or scope is null;

create unique index if not exists ux_store_base_url_lower
    on tcglooker.store (lower(rtrim(base_url, '/')));
create index if not exists ix_store_enabled_id
    on tcglooker.store (is_enabled, id);
create index if not exists ix_store_owner_user
    on tcglooker.store (owner_user_id) where owner_user_id is not null;
create index if not exists ix_store_created_by_user
    on tcglooker.store (created_by_user_id) where created_by_user_id is not null;

commit;
