#!/bin/sh
set -e

# Run as root only when it is explicitly requested with PUID=0 AND PGID=0.
if [ "$PUID" = "0" ] && [ "$PGID" = "0" ]; then
  chown -R 0:0 /app 2>/dev/null || true
  exec "$@"
fi

# Any other combination that is not a clean non-root pair (both set and non-zero) is a
# misconfiguration; fail fast instead of silently falling back to running as root.
if [ -z "$PUID" ] || [ -z "$PGID" ] || [ "$PUID" = "0" ] || [ "$PGID" = "0" ]; then
  echo "PUID and PGID must both be set to the same non-zero value, or both set to 0 to run as root." >&2
  exit 1
fi

userdel app 2>/dev/null || true
groupdel app 2>/dev/null || true

EXISTING_USER=$(getent passwd "$PUID" | cut -d: -f1)
if [ -n "$EXISTING_USER" ] && [ "$EXISTING_USER" != "app" ]; then
  userdel "$EXISTING_USER" 2>/dev/null || true
fi

EXISTING_GROUP=$(getent group "$PGID" | cut -d: -f1)
if [ -n "$EXISTING_GROUP" ] && [ "$EXISTING_GROUP" != "app" ]; then
  groupdel "$EXISTING_GROUP" 2>/dev/null || true
fi

groupadd -g "$PGID" app
useradd -u "$PUID" -g app -M app
chown -R app:app /app 2>/dev/null || true
exec gosu app "$@"
