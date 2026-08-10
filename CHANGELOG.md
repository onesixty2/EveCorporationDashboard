# Changelog

## Unreleased

- Next release zip should bundle Assets/OFL.txt (SIL Open Font License for the embedded
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
