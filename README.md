# Face Recognizer

Ansiktsgjenkjenning fra Reolink-kamera for Home Assistant. Modul A (denne tjenesten, .NET 10) lytter på en MQTT-trigger,
henter snapshot, gjenkjenner ansikt og sjekker liveness (IR), og publiserer resultatet på MQTT. Modul B er en
HA-automasjon (`ha/automations.example.yaml`) som låser/låser opp og varsler.

**Kjører helt lokalt.** Ingen tjeneste krever internett ved kjøring (kun bygging henter pakker/modellvekter).
Ansiktsgjenkjenning alene låser aldri opp: kreves liveness og, i automasjonen, en uavhengig faktor.

## Ansiktsmotor (`FACE_RECOGNITION_PROVIDER`)

| Verdi | Beskrivelse |
|---|---|
| `DeepFace` | Egen container (serengil/deepface, REST `/represent`), modell **SFace** + detektor **YuNet**. |
| `FaceAiSharp` | Innebygd ONNX i samme prosess, ingen ekstra container. |

Begge implementerer `IFaceRecognitionProvider` (deteksjon, registrering, gjenkjenning). Embeddings sammenlignes i .NET
(cosinus), holdes kun i minnet og publiseres/logges aldri. Ukjente ansikter gir `Unknown` (`personName: null`).
Flere ansikter: største ansikt vurderes, `faceCount` publiseres (automasjonen krever `faceCount == 1`).

### DeepFace: versjoner og lisenser
- Image: `serengil/deepface` v0.0.103 (MIT), **låst på digest** i `deepface/Dockerfile`.
- Vekter bakes inn med SHA-256 (`ADD --checksum`), containeren trenger ikke internett:
  - SFace `face_recognition_sface_2021dec.onnx`: Apache-2.0 (OpenCV Zoo).
  - YuNet `face_detection_yunet_2023mar.onnx`: MIT (OpenCV Zoo).
- Merk: lisensen på selve vektfilen er permissiv, men SFace er trent på et offentlig datasett (MS1M) med
  forskningsvilkår. Vurder dette ved kommersiell bruk. Unngått: ArcFace/Buffalo_L (insightface, ikke-kommersielle vekter),
  VGG-Face og Facenet (uklare/ikke-verifiserte vektlisenser).
- API-et kan kun nås med `Authorization: Bearer <DEEPFACE_AUTH_TOKEN>`; containeren starter ikke uten token.
- `/register` og `/search` brukes ikke (krever database). Registrerte personer lagres i stedet lokalt (se under).

### Registrere personer
Legg ett eller flere bilder per person i `faces/<person-id>/*.jpg` (volum `faces`, `/data/faces`; på HAOS
`/addon_configs/<slug>_face_recognizer/faces/`). Bildet skal ha ett tydelig ansikt. Lastes ved oppstart og
embeddings beregnes på nytt (bildene er den varige lagringen, så alt overlever restart).
I kode: `IFaceRecognitionProvider.RegisterAsync(personId, bytes)` lagrer bildet og aktiverer personen umiddelbart.

## Lokal test (Docker Compose, uten HAOS)
```powershell
Copy-Item .env.example .env        # fyll inn DEEPFACE_AUTH_TOKEN, CAMERA_*; .env ligger i .gitignore
docker compose --profile dev up -d --build   # dev = Mosquitto-testbroker på internt nett
```
Nettverk: `internal` (`internal: true`, ingen vei ut) har DeepFace og Mosquitto; kun `recognition` er også på `lan` for å nå
kameraet. Ingen porter publiseres.

### 1. Test kamerakontakt (anbefalt første steg)
1. Finn kameraets sertifikat-fingeravtrykk (kun TLS-handshake, ingen innlogging):
   ```powershell
   $c=New-Object Net.Sockets.TcpClient('kamera.local',443); $s=New-Object Net.Security.SslStream($c.GetStream(),$false,({$true}))
   $s.AuthenticateAsClient('kamera.local'); ([Security.Cryptography.X509Certificates.X509Certificate2]$s.RemoteCertificate).GetCertHashString('SHA256')
   ```
   Kameraet til dette prosjektet ga `D0F5937FDF67BB73F342B5ABD108BC33843EE55AF3641A48A83C453A7B884D40`
   (selvsignert Reolink-sertifikat). Sett i `CAMERA_CERT_SHA256`. Feil verdi avvises, og tom verdi avviser alt.
2. Opprett en egen kamerabruker (ikke admin) og fyll `CAMERA_USERNAME` / `CAMERA_PASSWORD` i `.env`.
3. Kjør selvtesten (ingen MQTT/HA nødvendig):
   ```powershell
   docker compose run --rm recognition selftest        # snapshot, motor, liveness
   docker compose run --rm recognition selftest --ir   # i tillegg IR-bytte (gjenoppretter Auto)
   ```
   Bildene lagres i `selftest-out/` (snapshot.jpg og snapshot-ir.jpg) for visuell kontroll.
   Utgangskoder: 0 ok, 2 ingen pin, 3 nettverk/sertifikat, 4 innlogging, 5 ansiktsmotor.

### 2. Test hele flyten
```powershell
docker compose exec mosquitto mosquitto_sub -t "facerecognizer/#" -t "homeassistant/#" -v   # i ett vindu
docker compose exec mosquitto mosquitto_pub -t facerecognizer/trigger -m go                  # i et annet
```
Resultatet (`facerecognizer/result`, retained):
`{"outcome":"verified|unknown|livenessFailed|noFace|error","personName":"Ola","score":0.71,"timestamp":"…","livenessScore":0.93,"cameraName":"inngang","faceCount":1,"provider":"DeepFace"}`.
Ingen bilder eller embeddings. Alle feil gir `error` (døren forblir låst).

## Home Assistant OS
HAOS kjører ikke Compose. Bruk to apper (kopier til `/addons/` via Samba):
1. `deepface/` → `/addons/deepface` (app «DeepFace», vert `local-deepface`, port 5000 kun internt). Sett `auth_token`.
2. Resten av repoet (uten `deepface/`, `.git`, `testdata`) → `/addons/face_recognizer`. Sett `face.provider: DeepFace`,
   `deepface.auth_token` (samme token), `camera.*` og `camera.certificate_sha256`. MQTT hentes automatisk fra Mosquitto-appen.
Se `DOCS.md` og `ha/automations.example.yaml`.

## MQTT Discovery
Sensorene «Utfall», «Person» og «Score» opprettes automatisk (device «Face Recognizer»). Tilgjengelighet via
`facerecognizer/status` (LWT).

## Helse
`MqttHealthCheck` (Unhealthy ved frakoblet) og `DeepFaceHealthCheck` (Degraded når DeepFace er nede) kjøres hvert minutt og
logges ved endring (`Helsestatus: …`). Ingen helse-port åpnes. DeepFace nede gir `error`-resultat per trigger (døren låst);
øvrige deler fortsetter, og referanser lastes på nytt ved første vellykkede kall.

## Kalibrering (må gjøres med egne bilder)
- `DEEPFACE_THRESHOLD` (cosinus-likhet, standard 0.5, SFace sin egen anbefaling er 0.407).
- `Liveness:MinScore` (standard 0.8). MiniFASNet er RGB-trent; nøyaktighet på IR er ikke verifisert.
- Skjult kjent begrensning: IR-bildet verifiseres ikke som samme person som fargebildet.
