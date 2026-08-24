#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# Apply sql/*.sql in filename order, once each, recording what was applied.
#
# Why this exists
# ---------------
# The schema was being maintained two ways, neither of which works for a fresh
# deployment:
#
#   • docker-entrypoint-initdb.d — the compose file mounts 001 through 011 there.
#     Postgres runs that directory ONLY when the data volume is empty, so it does
#     nothing on an upgrade, and it was never extended past 011.
#   • By hand — 012 through 022 were applied with `psql` against the running
#     database as they were written. That works exactly once, on one machine.
#
# So a new EC2 instance would boot with a schema missing flow_bars_15m, the
# per-trade exit geometry, the entry cap, risk sizing and both readiness views —
# and the bot would start, report healthy, and abstain forever on a table that does
# not exist. Precisely the silent failure this project keeps producing.
#
# Idempotency
# -----------
# Every file is written to be re-runnable (IF NOT EXISTS / CREATE OR REPLACE / DO
# blocks that check pg_constraint), but this does not rely on that: it records each
# applied filename with its SHA-256 and skips files already recorded. The checksum
# is what catches the dangerous case — a migration edited after it was applied
# somewhere, which would otherwise diverge silently between machines.
#
# Exits non-zero on the first failure. A half-migrated database must stop the
# deployment, not proceed to start services against it.
# ─────────────────────────────────────────────────────────────────────────────
set -eu

SQL_DIR="${SQL_DIR:-/sql}"
PSQL="psql --no-psqlrc --quiet -v ON_ERROR_STOP=1"

: "${PGHOST:?PGHOST is required}"
: "${PGUSER:?PGUSER is required}"
: "${PGDATABASE:?PGDATABASE is required}"

echo "migrate: waiting for ${PGHOST}:${PGPORT:-5432}/${PGDATABASE}"
until pg_isready -q -h "$PGHOST" -p "${PGPORT:-5432}" -U "$PGUSER" -d "$PGDATABASE"; do
    sleep 2
done

# ── Verify the credentials, not just the port ────────────────────────────────
#
# pg_isready above only asked whether the server accepts connections; it does not log
# in. And the postgres healthcheck cannot cover this either — the official image
# grants `trust` to local-socket and loopback connections, so a query run inside that
# container succeeds whatever the password is.
#
# This container connects over TCP, so it is the first thing in the stack that
# genuinely authenticates. Which makes it the right place to say so plainly: a
# mismatch here previously surfaced as a bare psql exit 2, and then as "container
# processor is unhealthy" — neither of which points at a password.
#
# The usual cause is that POSTGRES_PASSWORD only applies to an EMPTY data volume.
# Changing the secret against an existing volume does not change the database
# password; the volume has to be recreated or the old value restored.
if ! $PSQL -c 'SELECT 1' >/dev/null 2>&1; then
    echo "migrate: FAIL cannot authenticate to ${PGHOST}/${PGDATABASE} as '${PGUSER}'." >&2
    echo "         The server is up — pg_isready passed — so this is credentials." >&2
    echo "         POSTGRES_PASSWORD is applied by initdb only when the data volume is" >&2
    echo "         empty. If the volume predates the current password, either restore" >&2
    echo "         the old value or recreate the volume (docker compose down -v)." >&2
    exit 1
fi

# The ledger itself, created outside the ledger — it cannot record its own creation.
$PSQL <<'SQL'
CREATE TABLE IF NOT EXISTS schema_migrations (
    filename    TEXT        PRIMARY KEY,
    checksum    TEXT        NOT NULL,
    applied_at  TIMESTAMPTZ NOT NULL DEFAULT now()
);
COMMENT ON TABLE schema_migrations IS
    'One row per applied sql/*.sql file. Checksum catches a migration edited after '
    'it was applied, which is how two machines silently diverge.';
SQL

applied=0
skipped=0

# Sorted by name, so the numeric prefixes define the order. Two files sharing a
# prefix (005 appears twice in this repo) still both run — the filename is the key,
# not the number.
for path in $(find "$SQL_DIR" -maxdepth 1 -name '*.sql' | sort); do
    file=$(basename "$path")
    sum=$(sha256sum "$path" | cut -d' ' -f1)

    recorded=$($PSQL -tAc \
        "SELECT checksum FROM schema_migrations WHERE filename = '$file'")

    if [ -n "$recorded" ]; then
        if [ "$recorded" != "$sum" ]; then
            echo "migrate: FAIL $file was applied with a different checksum." >&2
            echo "         recorded $recorded" >&2
            echo "         on disk  $sum" >&2
            echo "         An applied migration was edited. Add a new file instead;" >&2
            echo "         editing one in place makes this database and the next" >&2
            echo "         disagree about what the schema is." >&2
            exit 1
        fi
        skipped=$((skipped + 1))
        continue
    fi

    # Adopting the ledger into a database that was already migrated by hand.
    #
    # This repo's development database has 001 through 022 applied manually. Running
    # them again would mostly work — they are written idempotently — but "mostly" is
    # not a property to bet a schema on, and 021/022 drop and recreate views that the
    # bot reads. Baseline mode records the files as present without executing them.
    #
    # Only ever use this on a database you have verified matches the files. On an
    # empty one it produces a ledger that claims a schema which does not exist.
    if [ "${MIGRATE_BASELINE:-0}" = "1" ]; then
        echo "migrate: baseline $file (recorded, not executed)"
        $PSQL -c "INSERT INTO schema_migrations (filename, checksum) VALUES ('$file', '$sum')"
        applied=$((applied + 1))
        continue
    fi

    echo "migrate: applying $file"

    # Each file in its own transaction, so a failure leaves the ledger consistent
    # with the schema rather than claiming a partial apply succeeded. The insert is
    # inside the same transaction as the DDL for the same reason.
    $PSQL --single-transaction \
        -c "\\i $path" \
        -c "INSERT INTO schema_migrations (filename, checksum) VALUES ('$file', '$sum')"

    applied=$((applied + 1))
done

echo "migrate: done — $applied applied, $skipped already present"
