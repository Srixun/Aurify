# Privacy

This document describes the Aurify 2.0.6 Windows client. The private Auratism website and the independent ForeverLogs service have their own server-side practices; review the [ForeverLogs privacy policy](https://foreverlogs.gg/privacy-policy) before connecting that optional provider.

## Default state and consent

Auratism data uploads are disabled by default until the user chooses **Pair with Discord** and explicitly approves the device code in the browser. Requesting and polling a pairing code necessarily sends the device label (`Aurify`) and pairing identifiers to `auratism.com`, but game data is not uploaded before approval.

ForeverLogs combat-log uploads are independently disabled until the user chooses **Connect ForeverLogs** and approves the `upload` scope in the browser. Choosing **Connect ForeverLogs** starts an OAuth/PKCE authorization request and sends the normal request metadata described below to `foreverlogs.gg` before approval; no combat log is uploaded unless that connection is approved. Connecting one provider does not connect the other.

## Data transferred to Auratism

Destination: the configured production origin, `https://auratism.com`, over HTTPS.

Depending on available game data and enabled operations, Aurify transfers:

- pairing messages, a device label, device and source identifiers, and the bearer device credential on authenticated requests;
- request metadata visible to an HTTPS service, such as IP address, time, route, and user agent;
- the selected WoW Forever client product, version/build key, addon versions, interface version, and observation times;
- validated addon snapshots for professions, guild/roster and character identity, character equipment and appearance/customization, activity, and Overlord data. These may include character names, GUIDs, realms, factions, guild membership, item identifiers/strings, profession progress, encounters, groups, and timestamps;
- combat-log stream identifiers, byte offsets, original complete combat-log bytes, capture time, log date, time-zone offset, and closed/open status. Combat logs can contain player names and combat activity;
- character-model job/catalog data and generated GLB previews, including relevant character appearance, equipment, and local client-build identifiers; and
- authenticated requests for addon metadata/packages, updates, and the separately downloaded model runtime.

The private server checks device revocation and authorizes every protected request. Discord membership/role authorization may be cached for up to five minutes; the client does not treat its saved credential or local checks as authorization.

## Data transferred to ForeverLogs

Destination: `https://foreverlogs.gg`, over HTTPS, only after the separate provider connection.

Aurify transfers:

- an OAuth/PKCE authorization request and callback data, followed by authorization-code, refresh-token, and revocation requests;
- the device label `Aurify`, redirect URI, requested `upload` scope, and protocol state/verification values;
- report title, private/public visibility, IANA time zone, report and segment identifiers, status/heartbeat/finalization messages; and
- gzip-compressed complete combat-log lines. Combat logs can contain player names and combat activity.

ForeverLogs returns account identity, access/refresh credentials, and report identifiers. The refresh credential and returned account identity are stored locally with Windows DPAPI protection for the current Windows user. Reports default to private; choosing public affects future reports only and requires a confirmation in Aurify.

## Local data

Aurify reads the selected WoW installation, supported addon saved variables, the combat log, and local game data needed for character previews. It stores configuration, DPAPI-protected credentials, upload checkpoints/outboxes, downloaded packages/runtime files, update state, and sanitized diagnostics under its per-user installation/data area. Character-model worker inputs and outputs are placed in private local work directories and cleaned after use when safe to do so. The model worker uses bundled, hash-verified listfile, TACT-key, and DBD-definition snapshots and does not contact the public metadata source projects at runtime.

## Controls

- **Exit Aurify** drains active work and stops the client. No new client transfer occurs while it is not running. The Startup shortcut launches it again at the next sign-in unless it is uninstalled.
- On the Auratism members page, **Revoke** disables the selected device at the server. Each subsequent protected request is checked; removing a displayed device is separate from revocation.
- **Cancel sign-in** cancels a pending ForeverLogs browser connection.
- **Disconnect ForeverLogs** first removes the local provider credential, then attempts remote revocation. If remote sign-out cannot be confirmed, Aurify remains disconnected locally and reports that condition.
- **Uninstall Aurify** removes the managed application and shortcuts, but intentionally preserves pairing, settings, checkpoints, game addons, pending data, and changed or unrecognized files. Uninstall is therefore not an account-revocation or data-erasure control.

Aurify does not promise a server deletion or retention period that the implemented client cannot enforce. For server-held data or account actions beyond the controls above, use the relevant provider's available account controls and current policy.
