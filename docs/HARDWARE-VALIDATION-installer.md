# Manual validation — installer, feedback, update check

Run after the first successful tag build (dry run: tag `v0.9.0`).

## One-time prerequisites
- [ ] Feedback worker deployed + secret set (infra/feedback-worker/README.md steps 1-5)
- [ ] Worker smoke tests pass (curl matrix in that README)
- [ ] `FeedbackService.EndpointUrl` matches the deployed worker URL

## Release pipeline (tag v0.9.0, push, watch Actions)
- [ ] `git tag v0.9.0 && git push origin v0.9.0` → Release workflow goes green
- [ ] Release page shows `StompStationManager-0.9.0-x64.msi` + SmartScreen note in body

## Installer (on a machine or fresh Windows account, ideally NOT the dev box)
- [ ] Download .msi from the release page; SmartScreen appears; More info → Run anyway works
- [ ] Install completes with NO admin/UAC prompt
- [ ] Start Menu entry "StompStation Manager" with the pedal icon; window/taskbar icon correct
- [ ] Title bar shows "StompStation Manager v0.9.0"
- [ ] App connects to the pedal and lists presets (core function intact in packaged build)
- [ ] Re-run same .msi → repair/no-op, not a second install
- [ ] Settings → Apps shows one entry with icon; uninstall removes app folder + shortcut

## Upgrade path
- [ ] Tag `v0.9.1`, install its .msi OVER v0.9.0 → version in title updates, single Apps entry

## Feedback end-to-end
- [ ] Send Feedback from the installed app → issue appears with `user-feedback` label,
      name/email/version/OS block intact
- [ ] Disconnect network, send → inline error, typed text preserved, retry works after reconnect

## Update check
- [ ] With v0.9.0 installed and v0.9.1 released: launch → banner "Version 0.9.1 is available."
- [ ] Download opens the release page; Dismiss hides banner for the session
- [ ] Delete the v0.9.0/v0.9.1 releases + tags after validation (keep the repo clean for v1.0.0)
