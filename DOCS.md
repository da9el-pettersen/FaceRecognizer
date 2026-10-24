# Face Recognizer

Modul A: gjenkjenner ansikt fra Reolink-kameraet, sjekker liveness i IR og publiserer resultatet på MQTT.
Selve låsehandlingen gjøres av en HA-automasjon (se `ha/automations.example.yaml`).

## Innstillinger (under Konfigurasjon)
- **camera.base_url / username / password**: kameraet. Passordet lagres av Home Assistant, ikke i imaget.
  Du kan også bruke `!secret` fra `secrets.yaml` i YAML-modus.
- **camera.certificate_sha256**: SHA-256 av kameraets sertifikat. Uten denne avvises alle kall mot kameraet.
- **face.provider**: `DeepFace` (egen app) eller `FaceAiSharp` (innebygd). **face.threshold** gjelder FaceAiSharp (standard 0,98).
- **deepface.base_url / auth_token / model_name / threshold**: DeepFace-appen (`http://local-deepface:5000`), samme token som i DeepFace-appen, modell SFace, minste cosinus-likhet (standard 0,5).
- **camera.name**: navn som tas med i resultatet.
- **liveness.min_score**: minste gjennomsnittlige «ekte»-score fra liveness-modellene (standard 0,8). Må kalibreres.
- **mqtt.***: valgfritt. Utelatt = MQTT-tilkobling hentes automatisk fra Mosquitto-appen.

## Referansebilder
Legg bilder i app-ens konfigurasjonsmappe: `/addon_configs/<slug>_face_recognizer/faces/<navn>/*.jpg`.
Appen laster dem ved oppstart. Alle bilder skal ha ett tydelig ansikt.

## DeepFace-appen
Installer `deepface/`-mappen som egen lokal app (`/addons/deepface`), sett `auth_token` og start den før Face Recognizer.
Den åpner ingen porter og trenger ikke internett (vektene er bakt inn).

## Selvtest
Se README.md (`selftest`) for å teste kamerakontakt lokalt før appen kobles til Home Assistant.

## MQTT
- Trigger (inn): `facerecognizer/trigger`
- Resultat (ut, retained): `facerecognizer/result`
- Sensorene «Utfall», «Person» og «Score» opprettes automatisk via MQTT Discovery.

## Installasjon (lokal app)
Kopier hele prosjektmappen (uten `.git`) til `/addons/face_recognizer` på HAOS, f.eks. via Samba-appen.
Velg deretter Innstillinger → Apper → Appbutikk → ⋮ → Se etter oppdateringer, og installer «Face Recognizer».
