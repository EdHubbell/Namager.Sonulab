# Privacy

Here is every network call the app makes.

## 1. Anonymous usage ping

So I can tell whether anyone actually uses NAMager — and therefore whether it's worth building
more of it — the app sends one small message when you connect your pedal.

**When:** the first time a pedal successfully connects in an app session, and at most once per
day. If you never connect a pedal, nothing is ever sent. Development builds never send anything.

**What, in full:**

| Field | Example | Why |
|---|---|---|
| `installId` | `8f3c1e64-…` | A random ID made on first run. Lets me count people instead of launches, and see whether they come back. Not derived from anything about you or your machine. |
| `appVersion` | `1.2.0` | Tells me how quickly people move to new releases. |
| `fw` | `2.5.1` | Which pedal firmware versions are in use, so I know what to keep supporting. |
| `transport` | `usb` | Which transport the app connected over. The app is USB-only, so this is always `usb` today; the field stays so a future transport can be measured. |

**What is never sent:** your name, email, IP address, preset/amp/IR names, file paths, device
serial numbers, or anything about what you do inside the app. Your IP is used only to rate-limit
abuse at the server and is never stored.

**Turning it off:** Settings ▸ Send anonymous usage ping. It is on by default. Turning it off
stops the ping immediately and permanently — nothing is queued or sent later. Deleting
`%APPDATA%\Namager\usage.json` additionally resets your install ID.

## 2. Update check

On launch the app asks GitHub's public API for the latest release version. This is a normal
unauthenticated web request; GitHub sees it, I don't.

## 3. Send Feedback (only when you use it)

The Send Feedback dialog posts the name, email, and message **you type**, plus your app version
and OS, and creates a **public** GitHub issue. Don't put anything in it you wouldn't post
publicly.

## 4. Tone3000

If you sign in to Tone3000, that's between you and Tone3000 under their privacy policy. Your
token is stored locally, encrypted with Windows DPAPI.

## 5. Snapshots (.namsnap)

**File ▸ Export Snapshot** writes a copy of your pedal — presets, amps, and IRs, plus their
names — to a file you choose. Nothing is uploaded by the app; where that file goes afterward is
up to you.

NAMager also keeps `%APPDATA%\Namager\ir-index.json`, which remembers which Tone3000 IR a piece
of content came from, keyed by a hash of the IR's content — the seed for a future feature that
would show real names instead of whatever a slot was renamed to; nothing displays those names
today. **That hash is never sent over the network.** Exporting a snapshot does write it into the
backup's manifest, since it's how the app recognizes that IR again — so it travels with the file
wherever you take it.

NAMager also keeps `%APPDATA%\Namager\preset-usage-cache.json`, which stores, per connected pedal id,
each preset's name and the amp/IR names it references — so reconnecting shows usage highlights
instantly. **Local only; never transmitted.**
