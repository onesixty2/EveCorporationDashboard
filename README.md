# EVE Member Tracker

WPF desktop app for corp member participation, mining tax, and citadel fuel. It combines
ESI data with pages copied from the goonfleet manager into one view:

1. **ESI** (director login) - member tracking (last login/logoff, ship, location), the
   corporation mining ledger, corp structures, and citadel fuel from corp assets.
2. **Paps / forum activity** - copied from the member corp's auth group page.
3. **Pilot map** - character to forum name mappings, copied per corp (member and alt corps,
   discovered automatically from the manager's corp list page).

## Tabs

- **Overview** - one row per person: paps (90/60/30), mining badge, last EVE login,
  out-of-game presence, location, and a computed status. Click a row to expand their
  characters. A 💀 column flags anyone seen on EVE/forums/jabber/mumble within 30 days
  with zero paps. Top three miners across the ledger get ⛏ / 🧱 / 🏆.
- **Mining Ledger** - per frack (auto-detected 72h extraction windows) and citadel:
  stacked bars per account, ore composition and share-by-account pies, quantity table
  with the 15% owed, and a Discord-ready export.
- **Citadel Fuel** - every corp-owned structure with fuel blocks in bay, corp hangar
  reserve, strontium, and time remaining. Control towers (with system and estimated
  burn time, UNPOWERED flag) sort to the bottom.

## Statuses

| Status | Meaning |
|---|---|
| **AWOL** | No EVE login *and* no forum/jabber/mumble presence within the threshold |
| **AFK - LOA** | Not logging into EVE, but still active out of game |
| **AFK - Vacationer** | On the auth group but no characters in the corp structure |
| **AFK** | In game, but 0 paps in 90 days |
| **Inactive** | In game, but 0 paps in 60 days |
| **No Participation** | In game, but 0 paps in 30 days |
| **Low Participation** | In game with only 1 pap (or fewer) in 30 days |
| **Active** | Logging in and making paps |
| **Unmapped character** | In the corp per ESI, but not in the pilot map yet |

Thresholds are configurable in Settings. Light/dark theme toggles from the toolbar (🌙/☀).

## One-time setup

1. Register an "Authentication & API Access" application at
   <https://developers.eveonline.com/applications> with callback URL
   `http://localhost:53411/callback/` and these scopes:
   - `esi-corporations.track_members.v1`
   - `esi-universe.read_structures.v1`
   - `esi-industry.read_corporation_mining.v1`
   - `esi-assets.read_corporation_assets.v1`
   - `esi-corporations.read_structures.v1`
   - `esi-corporations.read_starbases.v1`
2. In Settings: paste the Client ID, then **Log in with EVE** using a character with the
   **Director** role (mining also wants Accountant, fuel expiry Station Manager - Director
   covers all of it).
3. In Settings: click **Auth Groups**, Ctrl+A / Ctrl+C on that page, then
   **Import group from clipboard** to capture the member corp's paps group.
4. In the **Pilot Map** window: click **Corps**, copy the manager's corp list page, and
   apply to build the corp list. Then for each corp: click its link, Ctrl+A / Ctrl+C on
   the page, and **Import from clipboard**.
5. **Refresh ESI data**, then **Paps** import. Repeat imports roughly every 90 days
   (the toolbar warns when the pilot map goes stale).

## Data & privacy

Everything is stored locally in `%APPDATA%\EveCorporationDashboard\` (settings incl. the EVE SSO
refresh token, plus all imported/fetched data). The only network calls are to EVE's SSO
and ESI. Settings has a delete-all that wipes everything except the login.
