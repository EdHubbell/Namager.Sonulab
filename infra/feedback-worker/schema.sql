-- Append-only ping log. The composite primary key is the server-side backstop for the
-- client's once-per-day gate: a reinstall, a clock change, or a replayed request cannot
-- double-count a day. Keeping raw rows means new metrics are a query, not a redeploy.
CREATE TABLE IF NOT EXISTS pings (
  install_id  TEXT NOT NULL,
  day         TEXT NOT NULL,          -- UTC date, YYYY-MM-DD
  app_version TEXT NOT NULL,
  fw_version  TEXT NOT NULL,
  transport   TEXT NOT NULL,
  PRIMARY KEY (install_id, day)
);

-- Rolled-up per-install state, upserted on each accepted ping.
CREATE TABLE IF NOT EXISTS installs (
  install_id     TEXT PRIMARY KEY,
  first_seen     TEXT NOT NULL,
  last_seen      TEXT NOT NULL,
  active_days    INTEGER NOT NULL DEFAULT 0,
  app_version    TEXT NOT NULL,       -- most recent seen
  fw_version     TEXT NOT NULL,       -- most recent seen
  last_transport TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_installs_last_seen  ON installs(last_seen);
CREATE INDEX IF NOT EXISTS idx_installs_first_seen ON installs(first_seen);
CREATE INDEX IF NOT EXISTS idx_pings_day           ON pings(day);
