#!/bin/sh
# Som HA-app leses tokenet fra app-innstillingene. Ellers brukes DEEPFACE_AUTH_TOKEN fra miljøet.
if [ -f /data/options.json ]; then
  DEEPFACE_AUTH_TOKEN="$(python -c 'import json; print(json.load(open("/data/options.json")).get("auth_token", ""))')"
  export DEEPFACE_AUTH_TOKEN
fi

if [ -z "$DEEPFACE_AUTH_TOKEN" ]; then
  echo "DEEPFACE_AUTH_TOKEN mangler, API-et ville vært uten autentisering. Avbryter." >&2
  exit 1
fi

cd /app/deepface/api/src || exit 1
exec sh entrypoint.sh
