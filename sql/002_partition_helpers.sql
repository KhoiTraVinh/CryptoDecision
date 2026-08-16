-- ============================================================
-- Migration 002: Partition maintenance helpers
-- These can be called manually or via pg_cron for ops teams
-- ============================================================

-- Function: ensure a specific day's trade partition exists
CREATE OR REPLACE FUNCTION ensure_trade_partition(target_date DATE)
RETURNS VOID AS $$
DECLARE
    partition_name TEXT;
    start_date     TEXT;
    end_date       TEXT;
BEGIN
    partition_name := 'trades_' || TO_CHAR(target_date, 'YYYY_MM_DD');
    start_date     := TO_CHAR(target_date,     'YYYY-MM-DD');
    end_date       := TO_CHAR(target_date + 1, 'YYYY-MM-DD');

    EXECUTE FORMAT(
        'CREATE TABLE IF NOT EXISTS %I PARTITION OF trades
         FOR VALUES FROM (%L::timestamptz) TO (%L::timestamptz)',
        partition_name, start_date, end_date
    );

    EXECUTE FORMAT(
        'CREATE INDEX IF NOT EXISTS ix_%s_symbol_time ON %I (symbol, trade_time DESC)',
        partition_name, partition_name
    );

    RAISE NOTICE 'Partition % ensured', partition_name;
END;
$$ LANGUAGE plpgsql;

-- Function: drop partitions older than N days (data retention)
CREATE OR REPLACE FUNCTION drop_old_trade_partitions(retain_days INT DEFAULT 90)
RETURNS INT AS $$
DECLARE
    cutoff    DATE := CURRENT_DATE - retain_days;
    rec       RECORD;
    dropped   INT  := 0;
BEGIN
    FOR rec IN
        SELECT schemaname, tablename
        FROM pg_tables
        WHERE schemaname = 'public'
          AND tablename LIKE 'trades_%'
          AND tablename ~ '^trades_\d{4}_\d{2}_\d{2}$'
    LOOP
        DECLARE
            partition_date DATE;
        BEGIN
            partition_date := TO_DATE(
                REGEXP_REPLACE(rec.tablename, '^trades_', ''),
                'YYYY_MM_DD'
            );
            IF partition_date < cutoff THEN
                EXECUTE FORMAT('DROP TABLE IF EXISTS %I', rec.tablename);
                RAISE NOTICE 'Dropped partition %', rec.tablename;
                dropped := dropped + 1;
            END IF;
        EXCEPTION WHEN OTHERS THEN
            RAISE NOTICE 'Could not parse date from %', rec.tablename;
        END;
    END LOOP;
    RETURN dropped;
END;
$$ LANGUAGE plpgsql;

-- View: partition sizes for monitoring
CREATE OR REPLACE VIEW v_partition_sizes AS
SELECT
    child.relname                                       AS partition,
    pg_size_pretty(pg_relation_size(child.oid))         AS size,
    pg_relation_size(child.oid)                         AS size_bytes,
    (SELECT COUNT(*) FROM pg_inherits WHERE inhrelid = child.oid) AS is_partition
FROM pg_inherits
JOIN pg_class parent ON pg_inherits.inhparent = parent.oid
JOIN pg_class child  ON pg_inherits.inhrelid  = child.oid
WHERE parent.relname = 'trades'
ORDER BY child.relname DESC;

-- View: whale trade summary per day
CREATE OR REPLACE VIEW v_whale_summary AS
SELECT
    symbol,
    DATE(trade_time) AS trade_date,
    COUNT(*)         AS whale_count,
    SUM(quote_qty)   AS total_whale_volume,
    AVG(quote_qty)   AS avg_whale_size,
    MAX(quote_qty)   AS max_whale_size
FROM trades
WHERE is_whale = true
GROUP BY symbol, DATE(trade_time)
ORDER BY trade_date DESC, total_whale_volume DESC;
