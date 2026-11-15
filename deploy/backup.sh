#!/bin/sh
# Backup της Postgres μία φορά τη μέρα, κρατάει τις τελευταίες BACKUP_KEEP_DAYS μέρες.
# Τρέχει μέσα στο container "backup" (docker-compose.prod.yml).
set -eu
KEEP_DAYS="${BACKUP_KEEP_DAYS:-14}"

while true; do
  file="/backups/slotify-$(date +%Y%m%d-%H%M).dump"
  if pg_dump -h db -U slotify -d slotify -Fc -f "$file.tmp"; then
    mv "$file.tmp" "$file"
    echo "backup: $file ($(du -h "$file" | cut -f1))"
  else
    rm -f "$file.tmp"
    echo "backup: ΑΠΕΤΥΧΕ" >&2
  fi
  find /backups -name 'slotify-*.dump' -mtime "+$KEEP_DAYS" -delete
  sleep 86400
done
