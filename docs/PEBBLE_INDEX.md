# Pebble Index Transcript Receiver

Joydex can accept transcript-only webhooks from the Pebble Index 01 mobile app and forward each
accepted transcript to one deliberately selected local Codex task. The receiver is experimental,
disabled by default, and independent of an active Room Voice session.

## Network boundary

The [Pebble webhook contract](https://help.repebble.com/en/articles/15724406-index-advanced-features-mcp-webhook)
requires an HTTPS URL that the phone can reach. Joydex listens only on IPv4 loopback at
`http://127.0.0.1:<port>/pebble-index`, so the copied local endpoint cannot be entered directly in
the mobile app. Put a TLS reverse proxy or authenticated tunnel in front of it and forward only the
webhook path to Joydex. Joydex does not create or secure that public-facing layer.

Use the generated `Authorization` header as an additional secret at the proxy and receiver. Anyone
who obtains it can submit text to the selected Codex task. Keep the secret out of logs and shared
configuration. The unauthenticated `GET /health` endpoint reports only whether the receiver is up.

The Desktop Task Bridge starts only while configuration is open or a task-messaging feature is
enabled. It uses a randomized per-Joydex-session named pipe restricted to the current Windows user.
Processes running as that user are inside the local trust boundary; do not run untrusted software in
that user session while task messaging is enabled.

Configure the Index webhook to send **Transcription** only. Joydex rejects multipart parts marked
as file uploads and never stores audio.

## Configure Joydex

1. Open **Configure Joydex → Pebble Index**.
2. Select a Codex task. If no task is already listed, paste an existing Codex task UUID into
   **Codex task or task ID**, then choose **Refresh tasks** and select the intended task.
3. Choose the loopback port, enable the receiver, and save.
4. Use **Copy local endpoint** when configuring the local side of the HTTPS proxy or tunnel.
5. Use **Copy Authorization header** and add that complete value as the Pebble webhook's custom
   `Authorization` header.
6. Enter the proxy's public HTTPS URL in the Pebble app and send a short transcription to verify the
   selected task.

The pasted or saved task ID supplies a legitimate source identity to the constrained Desktop Task
Bridge. The public HTTP endpoint cannot list, select, or read Codex tasks.

## Request contract

The receiver accepts an authenticated `POST /pebble-index` with `multipart/form-data` and these
fields:

| Field | Requirement |
| --- | --- |
| `transcription` | Required UTF-8 text, 1–4,000 characters |
| `recordedAt` | Required Unix timestamp in milliseconds |
| `client` | Required printable label, 1–64 characters |

The standard Index request does not include a delivery ID. Joydex derives a stable identity from
the timestamp, client, trigger header, and transcript. A proxy may supply a stable
`X-Index-Delivery-Id`; reusing one with a different payload is rejected. `X-Index-Trigger` may carry
an optional trigger label.

Joydex limits request headers to 16 KiB, the multipart body to 64 KiB, concurrent connections to
eight, and each request to 15 seconds.

## Delivery states and recovery

Every accepted transcript is written below `pebble-index/inbox` beside the active Joydex
configuration before Joydex returns `202 Accepted`. These JSON records contain the plaintext
transcript and are retained until you delete them. Keep the Joydex data directory private and out of
shared or synced folders. **Open inbox** in Pebble Index settings opens the records for review or
manual cleanup.

- `sent` means Codex Desktop confirmed the send or queued it to a running task.
- `received` means the transcript is durable and was held before a send began.
- `deliveryUncertain` means a send began but confirmation did not return.

Duplicates never trigger another send. Joydex does not automatically replay `received` records; this
keeps webhook handling to at most one live attempt and makes operator recovery explicit. It also does
not retry `deliveryUncertain` records because the original send may already have reached Codex.
Settings show the outstanding count and latest detail. Inspect the JSON record, check the target task,
and copy the transcript manually when recovery is needed. Stop the receiver before deleting reviewed
records. Deleting a record also forgets its duplicate identity, so do not resubmit that old webhook.
