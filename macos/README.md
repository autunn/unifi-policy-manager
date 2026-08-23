# UniFi Policy Manager for macOS

Native macOS port built with SwiftUI and the official Ubiquiti Integration API.

## Requirements

- macOS 14 or later
- UniFi Cloud Gateway / Network application exposing the Integration API
- API Key created in the local Console under `Integrations`, or in `unifi.ui.com -> Settings -> API Keys`

## Included

- Independently collapsible Sidebar groups for workspace, network resources, policy/security, and infrastructure/developer modules
- UniFi-style visual resource pages for ordinary modules: icons, live metrics, state/type charts, category tabs, search tables and native inspectors
- Field-based forms for all supported create/update/action/delete operations, with automatic Site ID replacement and write confirmation
- A separate developer workbench for all 44 paths and 73 GET/POST/PUT/PATCH/DELETE operations, including raw JSON only when explicitly opened
- Native connection and multi-site selection
- DNS records: forward domain, A, AAAA, CNAME, MX, TXT and SRV CRUD
- DNS batch import from TXT, CSV and XLSX with preview, validation and de-duplication
- Bundled 212-domain forwarder preset and built-in CSV template
- Forward-domain batch deletion
- ACL and firewall policy list, JSON create/edit, enable/disable and delete
- ACL and firewall user-policy ordering
- Full policy change center with cross-platform baseline import, safe diff execution,
  strict synchronization, snapshot recovery and ordering restoration
- System and derived policies remain read-only
- API Key storage in macOS Keychain
- Automatic live DNS, ACL and firewall baseline snapshots before write operations
- Generic API writes show the final path/body for confirmation and log only the operation ID and method, never the request body
- Manual baseline export
- Offline demo mode

Backups and operation logs are stored in:

```text
~/Library/Application Support/UniFiPolicyManager
```

## Build

The source is a Swift Package. Install Xcode or Apple Command Line Tools, then run:

```bash
swift test --package-path macos
./macos/build-app.sh
```

The application is written to `macos/dist/UniFi-Policy-Manager.app`, enables
Hardened Runtime, and receives an ad-hoc local signature when no Developer ID
identity is configured. GitHub Actions packages the bundle with `ditto` so
executable permissions and macOS metadata are preserved. The release workflow
automatically performs Developer ID signing and Apple notarization when the
secrets documented in the repository root `SIGNING.md` are available.

Run the Swift package directly in offline demo mode with:

```bash
swift run --package-path macos UniFiPolicyManagerMac --demo
```

## Feature scope

The macOS and Windows editions cover the same complete official Network
Integration API scope: 44 paths and 73 operations, plus dedicated DNS, ACL and
firewall workflows, DNS batch management, policy ordering and the complete
policy change center. Features without a public Network API endpoint,
such as NAT, policy-based routing, port forwarding, QoS and static routes, are
intentionally not implemented on either platform.

## License

MIT. See the repository root `LICENSE`.
