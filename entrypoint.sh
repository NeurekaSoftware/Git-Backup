#!/bin/sh
set -e

# /app/data is the persisted mirror cache and can hold hundreds of thousands of files, so a recursive
# chown on every start would make startup grow with the very cache that exists to keep runs cheap.
# Fix the small, image-owned paths unconditionally, and only walk the cache when its ownership is
# actually wrong (first run, or PUID/PGID changed).
ensure_ownership() {
  owner="$1"

  chown "$owner" /app /app/bin /app/data 2>/dev/null || true
  chown -R "$owner" /app/bin 2>/dev/null || true

  if [ "$(stat -c %u:%g /app/data 2>/dev/null)" != "$owner" ]; then
    echo "Fixing ownership of /app/data for ${owner} (first run, or PUID/PGID changed)." >&2
    chown -R "$owner" /app/data 2>/dev/null || true
  fi
}

# Run as root only when it is explicitly requested with PUID=0 AND PGID=0.
if [ "$PUID" = "0" ] && [ "$PGID" = "0" ]; then
  ensure_ownership 0:0
  exec "$@"
fi

# Any other combination that is not a clean non-root pair (both set and non-zero) is a
# misconfiguration; fail fast instead of silently falling back to running as root.
if [ -z "$PUID" ] || [ -z "$PGID" ] || [ "$PUID" = "0" ] || [ "$PGID" = "0" ]; then
  echo "PUID and PGID must both be set to non-zero values, or both set to 0 to run as root." >&2
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
ensure_ownership "$PUID:$PGID"
exec gosu app "$@"
