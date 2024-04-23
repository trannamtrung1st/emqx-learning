CREATE EXTENSION IF NOT EXISTS timescaledb CASCADE;

CREATE TABLE IF NOT EXISTS device_metric_series (
    device_id varchar(255) NOT NULL,
    metric_key varchar(255) NOT NULL,
    value NUMERIC(100, 20) NOT NULL,
    _ts TIMESTAMP WITHOUT TIME zone,
    retention_days int null default 90
);

SELECT create_hypertable('device_metric_series','_ts', 'device_id' ,4, chunk_time_interval => INTERVAL '2 minutes');
