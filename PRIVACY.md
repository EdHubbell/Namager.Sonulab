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
| `transport` | `usb` | Whether anyone uses the WiFi connection, which is buggy and expensive to maintain. |

**What is never sent:** your name, email, IP address, preset/amp/IR names, file paths, device
serial numbers, or anything about what you do inside the app. Your IP is used only to rate-limit
abuse at the server and is never stored.

There is no opt-out toggle. If you'd rather not send it, don't use the app — or block
`namager-sonulab-feedback.ed-eed.workers.dev` at your firewall, which the app handles silently.
Deleting `%APPDATA%\Namager\usage.json` resets your install ID.

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
