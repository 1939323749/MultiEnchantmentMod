alter table public.telemetry_crashes
  add column if not exists installation_id text,
  add column if not exists mod_version text,
  add column if not exists game_version text,
  add column if not exists api_version integer,
  add column if not exists os_platform text,
  add column if not exists catalog_hash text,
  add column if not exists environment_hash text;

create index if not exists telemetry_crashes_created_at_idx
  on public.telemetry_crashes (created_at desc);

create index if not exists telemetry_crashes_mod_version_created_at_idx
  on public.telemetry_crashes (mod_version, created_at desc);

create index if not exists telemetry_crashes_environment_hash_created_at_idx
  on public.telemetry_crashes (environment_hash, created_at desc);

notify pgrst, 'reload schema';
