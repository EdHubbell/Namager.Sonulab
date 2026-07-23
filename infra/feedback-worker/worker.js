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

export default {
  async fetch(request, env) {
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
    if (recent.length >= MAX_PER_HOUR)
      return new Response('rate limited', { status: 429 });
    hits.set(ip, [...recent, now]);

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
  },
};
