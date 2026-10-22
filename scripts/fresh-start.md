# Bringing the stack up on a new box, from empty

Written 2026-10-21, when the EC2 instance was retired and everything moved to a
home server. Assumes empty Docker volumes — no database to carry over.

## What production actually had that git does not

Three layers hold configuration. Only one of them needed recovering:

| layer | where it lives | state |
| --- | --- | --- |
| compose files | `docker-compose.yml`, `docker-compose.prod.yml` | **in git.** Both were byte-identical to `949db7e` on the retired host — no drift to recover. |
| `.env` | written on the host by `deploy.yml` from GitHub secrets/vars | secrets are already in the local `.env`; one knob differed, see below |
| `bot_config` row 1 | the database | **not in git, not in any file.** Recovered into `restore-prod-bot-config.sql`. |

The strategy tuning people usually mean by "the config" — `TargetRiskMultiple`,
`UseRatchetExit`, `ReversalDropPct`, the H44/H45 settings — are all compose
defaults, so they came across with the repo and need nothing.

## Order of operations

`db-migrate` waits on `processor: service_healthy`, and that ordering is load
bearing. `DatabaseInitializer` is the sole owner of the base schema: it creates
`trades`, `bot_trades`, `klines_1m` and friends at runtime, and several `sql/`
files from 006 onward alter tables they do not create. Running `sql/` first
against an empty database fails at 006 on "relation bot_trades does not exist".
Compose already encodes the working order — just don't run `migrate.sh` by hand
against an empty database and conclude the chain is broken.

```bash
docker compose up -d --build        # builds from source; no GHCR needed locally
docker compose logs -f db-migrate   # wait for "migrate: done"
```

**Do not set `MIGRATE_BASELINE=1`.** It records migrations as applied without
executing them. It exists for adopting a database that was already migrated by
hand — which is what both the old dev and production databases were. Against an
empty volume it produces a ledger claiming a schema that does not exist.

## Restore the tuning

A fresh database seeds `bot_config` from the column defaults, then `023`
overwrites it with the "intended" baseline. That baseline is not what production
converged to; nine columns differ. After `db-migrate` reports done:

```bash
docker exec -i postgres psql -U crypto -d crypto < scripts/restore-prod-bot-config.sql
```

Verified on a throwaway database: after migrations plus this script, all 22
tuning columns match the retired production row exactly. The only two that
differ are `enabled` and `paper_mode`, which the script deliberately leaves
disarmed.

## Arming

The script leaves the bot off. Arming is a separate, deliberate act:

```sql
UPDATE bot_config SET enabled = TRUE, paper_mode = FALSE WHERE id = 1;
```

Check OKX connectivity and that `ingestion` is filling 15-minute buckets before
doing that — the strategy reads buckets, and an armed bot against an empty
`flow_bars_15m` just refuses every signal until the window fills.

## The one `.env` value that differed

`OKX_MAX_ORDER_NOTIONAL_USD` was **30** on production and **10** locally. It is a
hard cap applied after sizing, and production's last sizing note was
`0.28 SOLUSDT @ $105.27 = $29.48` — i.e. sitting just under the cap. At 10 every
order clips to a third of its intended size and the bot stops reproducing
production's behaviour. Synced to 30.

## What was deliberately NOT synced: Polymarket

The `POLY_*` values on the retired host are the inert ones. Polymarket geo-blocks
`ap-southeast-2`, so the service deployed there never placed an order; the
instance that actually rests orders is the local one. Copying those values down
would overwrite live, measured settings with numbers that never traded:

| key | retired host | local (live) |
| --- | --- | --- |
| `POLY_STAKE_USD` | 0.06 | **0.10** |
| `POLY_MIN_VOLUME_USD` | 50000 | **25000** |
| `POLY_ARM_BELOW_ASK` | 0.15 | **0.10** |

Local wins on all three. Note that the local `.env` now supersedes the
`${POLY_*:-default}` values in the compose file — the running container reads
`ArmBelowAsk=0.1` and `MinVolumeUsd=25000` from `.env`, not the 0.09 and 25000
written as compose defaults. Whichever box this moves to, carry the `.env`.
