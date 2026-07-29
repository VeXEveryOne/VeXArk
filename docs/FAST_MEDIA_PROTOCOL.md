# VeXArk Fast Media Protocol v1

Fast Media is an optional read-only data lane used by VeXArk 0.7.0 and newer
to copy MediaStore photos and videos over the local network. ADB remains the
authenticated control channel and the automatic fallback.

## Session setup

1. An already paired Desktop sends the signed `media_session_open` command over
   the loopback Agent protocol carried by ADB.
2. Desktop supplies a random 32-byte session key and requests up to four workers.
3. Agent binds an ephemeral port to the active private Wi-Fi IPv4 address.
4. Agent returns a random 16-byte session ID, address, port, expiry and worker
   limit.
5. Every worker proves possession of the session key before encrypted records
   are accepted.

The listener exists only during the transfer. It expires after 30 seconds
without traffic and is also closed by `media_session_close` or Agent shutdown.

## Cryptography

- HKDF-SHA256 derives independent AES keys for every worker and direction.
- Records use AES-256-GCM with a 128-bit authentication tag.
- Each direction has a strictly monotonic 64-bit counter.
- The nonce is four zero bytes followed by the big-endian record counter.
- Version, session ID, worker, direction, record type, counter and plaintext
  length are authenticated as associated data.
- A counter mismatch, modified ciphertext or invalid tag terminates the worker.

## Records

The binary record header contains:

| Field | Size |
| --- | ---: |
| Record type | 1 byte |
| Ciphertext plus tag length | 4 bytes, big-endian |
| Counter | 8 bytes, big-endian |

The maximum plaintext record is 1 MiB. Supported inner record types are
`OPEN`, `METADATA`, `DATA`, `END`, `ERROR` and `PROBE`.

Fast Media accepts only MediaStore image/video content URIs. It cannot execute
shell commands, read root paths, restore data or write to the phone.

## Compatibility

The signed ADB protocol remains version 1. Feature negotiation uses the
`media-export-v2` and `fast-lan-aead-v1` capabilities. A new Desktop falls back
to the legacy ADB exporter when either capability is unavailable.
