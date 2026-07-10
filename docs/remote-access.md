# Remote access (Phase 1)

Remote access is disabled by default and listens only on `127.0.0.1`. Do not forward its port through a router.

1. Install Tailscale, then sign in to it on the Windows PC and Android phone with the owner's identity.
2. Restrict Tailnet policy to the owner phone/device.

    For a personal tailnet, tag only the Windows PC as the host and allow only the owner's Tailscale identity to reach HTTPS. Replace the example email with the actual account identity before saving it in the Tailscale admin console:

    ```json
    {
        "tagOwners": {
            "tag:vrcx-host": ["owner@example.com"]
        },
        "grants": [
            {
                "src": ["owner@example.com"],
                "dst": ["tag:vrcx-host"],
                "ip": ["tcp:443"]
            }
        ]
    }
    ```

3. Start VRCX with `VRCX_REMOTE_ACCESS=1`; optionally set `VRCX_REMOTE_ACCESS_PORT` (default `36742`). After obtaining the Serve URL, set `VRCX_REMOTE_TAILNET_URL` to that exact `https://…ts.net` URL so the desktop status checklist can show it. In PowerShell, launch it with:

    ```powershell
    $env:VRCX_REMOTE_ACCESS = '1'
    $env:VRCX_REMOTE_TAILNET_URL = 'https://<machine>.<tailnet>.ts.net'
    Start-Process '<path-to-VRCX.exe>'
    ```

4. On the PC, configure a tailnet-only HTTPS reverse proxy. This is intentionally a manual operator step, so VRCX never changes Tailscale policy or starts public sharing:

    ```powershell
    tailscale serve --bg 127.0.0.1:36742
    ```

    Tailscale Serve proxies only the loopback service and reports the tailnet HTTPS URL. Do **not** use `tailscale funnel`; Funnel is public-internet exposure.

5. Capture `tailscale serve status --json` and the saved tailnet policy as operator evidence, then open the reported `https://<machine>.<tailnet>.ts.net/` URL on Android. If HTTPS is not enabled for the tailnet, enable MagicDNS and HTTPS certificates in the Tailscale admin console first.

To stop sharing, run `tailscale serve reset`. To stop VRCX's local listener, exit VRCX or relaunch it without `VRCX_REMOTE_ACCESS=1`.

Phase 1 exposes only current-user identity and read-only favorite-world browsing. It never exposes cookies, raw SQL, generic IPC, or arbitrary VRChat request URLs.

Tailscale Serve behavior and its current CLI syntax are documented by Tailscale: <https://tailscale.com/docs/reference/tailscale-cli/serve>. HTTPS prerequisites are documented at <https://tailscale.com/docs/how-to/set-up-https-certificates>.

## Validation record

The repository's automated validation covers loopback binding, typed API allowlisting, response redaction, origin rejection, remote asset isolation, and a production-build asset smoke test. The Windows package has also been verified to contain the remote server modules and `remote.html`.

Android-over-tailnet and non-owner-device denial must be checked after Tailscale is installed and the owner tailnet is available; they cannot be simulated by a local unit test. For the denial check, try the tailnet URL from a device signed out of Tailscale or outside the tailnet (it must be unreachable); if a second tailnet identity/device is available, confirm the policy denies it as well. Retain the policy and `tailscale serve status --json` output if a second device is unavailable.
