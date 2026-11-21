# Changelog

## Unreleased

### Fixed
- Pilot map imports never removed anyone, so a character who left the corp stayed in the
  map forever and kept appearing in reports. Each mapping now records the corp page it
  came from, and re-importing that page retires anyone it no longer lists. Other corps are
  untouched, and the removed names are listed in the import result rather than vanishing
  silently.

### Added
- Settings → Pilot map cleanup → "Clear untracked characters", for mappings imported
  before per-corp tracking existed. Those rows carry no source, so no corp re-import can
  retire them. Re-import each corp first (which adopts everyone still listed), then clear
  the remainder.

### Changed
- A character that moves between two tracked corps is re-tagged to the new corp on import
  instead of being duplicated, and re-importing the old corp no longer reports them as
  departed.
- Pilot map import logic moved out of the window into `Services/PilotMapImport.cs` so it
  can be tested directly.

## v1.3 (2026-11-21)

Window sizing release: the dashboard fits a larger roster.

### Changed
- The main window is freely resizable (it was fixed at 1010x900 with only a minimize box)
  and remembers its size, position, and maximized state across relaunches. A saved
  placement that no longer lands on a connected monitor is discarded rather than restored
  off-screen. Minimum size is 900x560.
- Default window width raised from 1010 to 1180 so the mining charts fit out of the box.
- The mining bar chart now takes the window's spare width instead of being capped at 620px
  with the leftover space going to an empty filler column. With a larger roster, widening
  the window shows more accounts; the ore and account pies keep their size on the right.

### Fixed
- "Share by account" pie was clipped off the right edge of the mining tab. The chart row
  needed 1112px inside a 1010px window, so the third column had nowhere to go.
- The UI scale slider no longer forces the window back to a computed size, which would
  have overwritten a size the user set by dragging the frame. It still grows the window to
  fit zoomed content, but only ever grows it.

## v1.2.1 (2026-08-15)

Hotfix for a startup crash in v1.2.

### Fixed
- Startup crash in the mining chart when one account had the same ore under more than one
  corp (an alt in another corp, or a corp change mid-frack). The chart built a per-ore
  lookup that assumed one row per ore, so the duplicate threw. Because the chart is built
  from the main window's constructor, this made the app unopenable once an ESI refresh had
  saved data in that shape - reinstalling did not help, since the data lives in %APPDATA%.
  The rows are now folded together per ore.

## v1.2 (2026-08-14)

Setup release: logging in no longer requires registering your own EVE application.

### Changed
- EVE SSO is now handled by the application itself. The app ships with its own registered
  EVE application (a public PKCE client, so no client secret is involved and the Client ID
  is safe to distribute), and users just click "Log in with EVE".
- Setup no longer requires registering an application at developers.eveonline.com or
  configuring a callback URL. The Client ID and Callback URL fields are gone from Settings,
  along with the registration link and scope list that went with them.

### Removed
- `AppSettings.ClientId` and `AppSettings.CallbackUrl`. Existing settings.json files keep
  working; the stale fields are ignored on load.

### Notes
- The release zip now bundles Assets/OFL.txt (SIL Open Font License for the embedded
  Oxanium font) alongside the exe.

## v1.1 (2026-08-08)

Accessibility and polish release.

### Added
- Oxanium font embedded app-wide (no install needed), with system-font fallback.
- UI scale slider on the toolbar (100% to 200%); the window and all dialogs grow with it,
  capped to the screen.
- Corp window icon is now downloaded and cached on disk: once a director logs in, the corp
  icon always shows (including offline launches) and overrides the built-in default.

### Changed
- Higher-contrast text in both light and dark themes.
- Overview simplified: slimmer column headers (90d / 60d / 30d, Mine); Location and
  "Seen anywhere" moved into the per-member character drill-down; no horizontal scrolling;
  fixed window size driven by the scale slider; launches with no row selected.
- Mining Ledger: the frack summary line moved below the charts so it can no longer clip.
- Citadel Fuel: cards use a three-line layout (name / time remaining / details) so the
  expiry date is always fully visible.

### Fixed
- Settings window crash introduced by the first scale-slider implementation.
- Scale control no longer shifts under the cursor while dragging.

## v1.0 (2026-08-08)

Initial release.

- Overview: per-member participation status (paps 90/60/30, mining badges, pap-drought
  ladder with AWOL / AFK / Inactive tiers), 💀 flag for members active without paps,
  character drill-down with per-character login, ship, and location.
- Mining Ledger: auto-detected fracks per citadel, stacked per-account bars, ore and
  account share pies, 15% owed table, Discord-ready export.
- Citadel Fuel: every corp-owned structure with fuel bay, corp hangar reserve, strontium,
  time remaining, and control towers (system, estimated burn, UNPOWERED flag).
- One-click clipboard imports for paps and the pilot map, with automatic column mapping
  and inline validation; corp roster synced from the manager's corp list page.
- EVE SSO login (PKCE), automatic ESI refresh on launch (weekly) and after imports.
- Light and dark (EVE-styled) themes; dynamic corp icon and window title.
