# Live validation — Tone3000 Browse Tones tab

**Spec:** docs/superpowers/specs/2026-07-07-tone3000-integration-design.md.
Steps 1–6 need only internet + keys; steps 7–9 need the pedal (VoidX-Control closed).

- [ ] 1. Launch with NO %APPDATA%\ToneManager\tone3000.json → tab shows the
       "add your keys" card with the exact path. Restore the file afterwards.
- [ ] 2. Sign in → browser opens tone3000.com authorize page → approve → app flips to
       "signed in as <username>". (First real PKCE round-trip — if the authorize page
       rejects the redirect_uri, note the registered-port requirement in tone3000.json
       and docs/tone3000-api-findings.md.)
- [ ] 3. Restart the app → still signed in (DPAPI refresh token survived).
- [ ] 4. Search "deluxe" → cards render (images or ♪ placeholders); NAM/IR chips filter;
       Favorites / Downloaded views load; pager works.
- [ ] 5. Select a tone → detail panel: title/author/gear/description/link/models.
       "Open on tone3000.com" opens the browser.
- [ ] 6. Disconnected: "Send to pedal" buttons are DISABLED with the tooltip.
- [ ] 7. [PEDAL] Connect. Send a NAM model → downloads to NAMFiles\Tone3000\ under the app's
       base directory (bin\Debug\net10.0\ when run via dotnet run), switches to
       the Amps tab, upload panel open with name + notes ("<title> by <author> (Tone3000)")
       + link prefilled. Complete the upload; check the amp's details pane shows the
       Tone3000 URL and notes in its SSMD metadata.
- [ ] 8. [PEDAL] Send an IR model → switches to IRs tab, panel prefilled with the filename;
       upload; confirm the IR works.
- [ ] 9. Sign out → signed-out card; sign back in.
- [ ] 10. Record date, findings, and any divergence into docs/tone3000-api-findings.md.
- [ ] 11. Keyboard nav: arrow keys in the nav list skip the disabled BROWSE TONES header (first disabled ListBoxItem in this codebase).
- [ ] 12. Connect while ON the Tone3000 tab, then Send to pedal immediately — the Amps tab must open with the upload panel prefilled (first-visit lazy-load ordering).
