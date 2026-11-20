-- Run in the Ranks database. Change this to the configured TableName if needed.
SET @ranks_table = 'lvl_base';
SET @hits_table = CONCAT(@ranks_table, '_hits');
SET @quoted_core = CONCAT('`', REPLACE(@ranks_table, '`', '``'), '`');

-- A full, single-column unique key also prevents concurrent first joins from creating two rows.
-- The ALTER fails if legacy SteamIDs are duplicated. Inspect those rows before retrying.
SET @has_steam_key = EXISTS (
    SELECT 1 FROM information_schema.statistics
    WHERE table_schema = DATABASE() AND table_name = @ranks_table AND non_unique = 0
    GROUP BY index_name
    HAVING COUNT(*) = 1 AND MAX(column_name) = 'steam' AND COUNT(sub_part) = 0
);
SET @ddl = IF(@has_steam_key, 'SELECT 1',
    CONCAT('ALTER TABLE ', @quoted_core, ' ADD UNIQUE INDEX ux_ranks_steam (`steam`)'));
PREPARE stmt FROM @ddl;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

SET @has_value_key = EXISTS (
    SELECT 1 FROM information_schema.statistics
    WHERE table_schema = DATABASE() AND table_name = @ranks_table
        AND seq_in_index = 1 AND column_name = 'value'
);
SET @ddl = IF(@has_value_key, 'SELECT 1',
    CONCAT('ALTER TABLE ', @quoted_core, ' ADD INDEX idx_ranks_value (`value`)'));
PREPARE stmt FROM @ddl;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;

-- ExStats Hits is optional. NULL lets the plugin reconstruct older rows from body-part counts.
SET @add_total = EXISTS (
    SELECT 1 FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = @hits_table
) AND NOT EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = DATABASE() AND table_name = @hits_table AND column_name = 'AllHits'
);
SET @ddl = IF(@add_total, CONCAT('ALTER TABLE `', REPLACE(@hits_table, '`', '``'),
    '` ADD COLUMN `AllHits` INT NULL'), 'SELECT 1');
PREPARE stmt FROM @ddl;
EXECUTE stmt;
DEALLOCATE PREPARE stmt;
