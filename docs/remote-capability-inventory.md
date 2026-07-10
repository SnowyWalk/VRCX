# Remote capability inventory

This inventory defines the browser boundary for VRCX Remote. It is intentionally narrower than the Electron renderer, whose `window.interopApi` bridge can dispatch arbitrary .NET classes and methods.

## Phase 1 allowlist

| Capability      | Endpoint                             | Backend action                                        | Output                                | State     |
| --------------- | ------------------------------------ | ----------------------------------------------------- | ------------------------------------- | --------- |
| Current user    | `GET /api/remote/v1/me`              | Fixed `WebApi.Execute` request to `/auth/user`        | `id`, `displayName`, `username`       | Read-only |
| Favorite worlds | `GET /api/remote/v1/favorite-worlds` | Fixed `WebApi.Execute` request to `/worlds/favorites` | world ID, name, author, thumbnail URL | Read-only |

The client supplies no URL, request method, headers, cookies, class name, method name, SQL, or file path. The server constructs both VRChat requests and projects the response fields.

## Explicitly unavailable in the browser

- Generic Electron/.NET IPC and arbitrary `WebApi.Execute` requests.
- Raw SQL, `SQLite`, `VRCXStorage`, cookies, authorization headers, account recovery data, and local logs.
- File pickers, clipboard, process/registry actions, update/restart, tray/window controls, notifications, and VR overlays.
- Any state-changing VRChat action, including friend, invite, moderation, group, avatar, and world mutation operations.

These operations remain desktop-only. They must not be emulated with browser globals.

## Admission rule for Phase 2+

Every added remote capability needs a named endpoint, fixed input/output schema, read/write and idempotency decision, redaction review, targeted tests, mobile UI behavior, and an explicit capability-boundary review before implementation. A new generic dispatch endpoint is never an acceptable shortcut.
