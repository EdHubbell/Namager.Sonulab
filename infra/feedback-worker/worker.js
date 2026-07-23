// NAMager for Sonulab feedback endpoint: turns an app POST into a GitHub issue.
// Deployed manually with `wrangler deploy`; secret GITHUB_TOKEN is a fine-grained PAT
// scoped to EdHubbell/Namager.Sonulab with Issues read/write ONLY.

const REPO = 'EdHubbell/Namager.Sonulab';
const CAPS = { name: 100, email: 200, message: 4000 };
const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

// Best-effort per-isolate rate limit (resets when the isolate recycles).
// The real backstop is the Cloudflare dashboard rate-limiting rule (see README).
const hits = new Map();
const MAX_PER_HOUR = 5;

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const TRANSPORTS = new Set(['usb', 'wifi', 'unknown']);
const pingHits = new Map();
const PING_MAX_PER_HOUR = 20;

// Both rate-limit maps below are keyed by IP and live for the isolate's lifetime. Without this
// cap, an isolate that serves many distinct IPs (telemetry volume in particular) grows the map
// forever, since a key was previously only ever pruned/overwritten, never removed. Chosen
// arbitrarily high enough to never affect real traffic; it only guards against unbounded growth.
const RATE_MAP_MAX_ENTRIES = 10000;

export default {
  async fetch(request, env) {
    const { pathname } = new URL(request.url);
    // "/" must keep behaving exactly as before: installed copies of the app POST feedback there.
    return pathname === '/ping' ? handlePing(request, env) : handleFeedback(request, env);
  },
};

async function handleFeedback(request, env) {
    if (request.method !== 'POST')
      return new Response('method not allowed', { status: 405 });
    if (!(request.headers.get('content-type') || '').includes('application/json'))
      return new Response('unsupported content type', { status: 415 });

    let f;
    try { f = await request.json(); } catch { return new Response('bad json', { status: 400 }); }

    // Guard against null or non-object JSON (e.g., JSON.parse('null') succeeds but f is null)
    if (!f || typeof f !== 'object') return new Response('bad json', { status: 400 });

    // Honeypot: bots fill every field. Pretend success so they don't adapt.
    if (f.website) return new Response(null, { status: 201 });

    for (const [field, max] of Object.entries(CAPS)) {
      if (typeof f[field] !== 'string' || !f[field].trim() || f[field].length > max)
        return new Response(`invalid ${field}`, { status: 400 });
    }
    if (!EMAIL_RE.test(f.email))
      return new Response('invalid email', { status: 400 });

    const ip = request.headers.get('cf-connecting-ip') || 'unknown';
    const now = Date.now();
    const recent = (hits.get(ip) || []).filter(t => now - t < 3600_000);
    // Write the pruned list back before checking the limit. This keeps each entry small and is
    // what persists on the 429 path, which returns before the re-set below. It does NOT shrink
    // the map: the delete branch is only reachable with an empty list, and such a request always
    // falls through to the re-set, which re-adds the key. Key count is bounded solely by the
    // RATE_MAP_MAX_ENTRIES cap below.
    if (recent.length === 0) hits.delete(ip); else hits.set(ip, recent);
    if (recent.length >= MAX_PER_HOUR)
      return new Response('rate limited', { status: 429 });
    hits.set(ip, [...recent, now]);
    if (hits.size > RATE_MAP_MAX_ENTRIES) hits.clear();

    const title = `Feedback: ${f.message.trim().slice(0, 60)}`;
    const body = [
      f.message.trim(),
      '',
      '---',
      `**Name:** ${f.name.trim()}`,
      `**Email:** ${f.email.trim()}`,
      `**App version:** ${typeof f.appVersion === 'string' ? f.appVersion.slice(0, 50) : 'unknown'}`,
      `**OS:** ${typeof f.os === 'string' ? f.os.slice(0, 200) : 'unknown'}`,
    ].join('\n');

    const gh = await fetch(`https://api.github.com/repos/${REPO}/issues`, {
      method: 'POST',
      headers: {
        'authorization': `Bearer ${env.GITHUB_TOKEN}`,
        'accept': 'application/vnd.github+json',
        'user-agent': 'namager-sonulab-feedback-worker',
        'content-type': 'application/json',
      },
      body: JSON.stringify({ title, body, labels: ['user-feedback'] }),
    });

    // Never leak GitHub response details (or the token's existence) to callers.
    return new Response(null, { status: gh.status === 201 ? 201 : 502 });
}

async function handlePing(request, env) {
  if (request.method !== 'POST')
    return new Response('method not allowed', { status: 405 });
  if (!(request.headers.get('content-type') || '').includes('application/json'))
    return new Response('unsupported content type', { status: 415 });

  let p;
  try { p = await request.json(); } catch { return new Response('bad json', { status: 400 }); }
  if (!p || typeof p !== 'object') return new Response('bad json', { status: 400 });

  // A strict GUID check keeps the table hard to pollute with invented or enumerated IDs.
  if (typeof p.installId !== 'string' || !GUID_RE.test(p.installId))
    return new Response('invalid installId', { status: 400 });
  for (const field of ['appVersion', 'fw']) {
    if (typeof p[field] !== 'string' || !p[field].trim() || p[field].length > 20)
      return new Response(`invalid ${field}`, { status: 400 });
  }
  if (typeof p.transport !== 'string' || !TRANSPORTS.has(p.transport))
    return new Response('invalid transport', { status: 400 });

  // IP is used for rate limiting ONLY and is never written to D1 (PRIVACY.md).
  const ip = request.headers.get('cf-connecting-ip') || 'unknown';
  const now = Date.now();
  const recent = (pingHits.get(ip) || []).filter(t => now - t < 3600_000);
  // Same pruning as the feedback route's `hits` map above, with the same caveat: this trims each
  // entry's timestamps (and is what persists on the 429 path) but never reduces the key count,
  // since an empty list always falls through to the re-set below. The cap is the real bound.
  if (recent.length === 0) pingHits.delete(ip); else pingHits.set(ip, recent);
  if (recent.length >= PING_MAX_PER_HOUR)
    return new Response('rate limited', { status: 429 });
  pingHits.set(ip, [...recent, now]);
  if (pingHits.size > RATE_MAP_MAX_ENTRIES) pingHits.clear();

  const day = new Date(now).toISOString().slice(0, 10);
  const id = p.installId.toLowerCase();

  try {
    // Both statements run as one batch (D1 batches are an implicit transaction), so the pings
    // insert and the installs upsert either both land or both roll back — no window where one
    // succeeds and the other doesn't.
    //
    // active_days is derived as (SELECT COUNT(*) FROM pings WHERE install_id = ?) rather than
    // incremented, so it is correct by construction: replaying the same day's ping is harmless
    // (INSERT OR IGNORE keeps pings unchanged, so the COUNT is unchanged), and there is no
    // dependency on D1's INSERT OR IGNORE change-count semantics. It is also self-healing: if an
    // install row is ever out of sync with pings (e.g. from data prior to this fix), the very
    // next ping recomputes active_days from the source of truth instead of drifting further.
    await env.USAGE_DB.batch([
      env.USAGE_DB.prepare(
        `INSERT OR IGNORE INTO pings (install_id, day, app_version, fw_version, transport)
         VALUES (?, ?, ?, ?, ?)`
      ).bind(id, day, p.appVersion, p.fw, p.transport),
      env.USAGE_DB.prepare(
        `INSERT INTO installs (install_id, first_seen, last_seen, active_days,
                               app_version, fw_version, last_transport)
         VALUES (?, ?, ?, (SELECT COUNT(*) FROM pings WHERE install_id = ?), ?, ?, ?)
         ON CONFLICT(install_id) DO UPDATE SET
           first_seen     = MIN(installs.first_seen, excluded.first_seen),
           last_seen      = MAX(installs.last_seen,  excluded.last_seen),
           active_days    = excluded.active_days,
           app_version    = excluded.app_version,
           fw_version     = excluded.fw_version,
           last_transport = excluded.last_transport`
      ).bind(id, day, day, id, p.appVersion, p.fw, p.transport),
    ]);
  } catch {
    // Never leak database errors to the client; the ping is disposable.
    return new Response(null, { status: 502 });
  }

  return new Response(null, { status: 204 });
}
