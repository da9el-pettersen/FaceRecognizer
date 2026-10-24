# Face Recognizer

Modul A: gjenkjenner ansikt fra Reolink-kameraet, sjekker liveness i IR og publiserer resultatet på MQTT.
Selve låsehandlingen gjøres av en HA-automasjon (se `ha/automations.example.yaml`).

## Innstillinger (under Konfigurasjon)
- **camera.base_url / username / password**: kameraet. Passordet lagres av Home Assistant, ikke i imaget.
  Du kan også bruke `!secret` fra `secrets.yaml` i YAML-modus.
- **camera.certificate_sha256**: SHA-256 av kameraets sertifikat. Uten denne avvises alle kall mot kameraet.
- **face.threshold**: minste likhet (standard 0,98).
- **mqtt.***: valgfritt. Utelatt = MQTT-tilkobling hentes automatisk fra Mosquitto-appen.

## Referansebilder
Legg bilder i app-ens konfigurasjonsmappe: `/addon_configs/<slug>_face_recognizer/faces/<navn>/*.jpg`.
Appen laster dem ved oppstart.

## MQTT
- Trigger (inn): `facerecognizer/trigger`
- Resultat (ut, retained): `facerecognizer/result`
- Sensorene «Utfall» og «Score» opprettes automatisk via MQTT Discovery.

## Installasjon (lokal app)
Kopier hele prosjektmappen (uten `.git`) til `/addons/face_recognizer` på HAOS, f.eks. via Samba-appen.
Velg deretter Innstillinger → Apper → Appbutikk → ⋮ → Se etter oppdateringer, og installer «Face Recognizer».
