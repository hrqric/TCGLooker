-- Presents timestamptz audit values in the application's local timezone.
-- PostgreSQL keeps the underlying instants timezone-safe while sessions render
-- started_at and finished_at with the America/Sao_Paulo offset.
begin;

alter database postgres set timezone to 'America/Sao_Paulo';

commit;
