# Face Recognizer

Ansiktsgjenkjenning fra Reolink-kamera for Home Assistant. Modul A (denne tjenesten, .NET 10) lytter på en MQTT-trigger,
leser RTSP-video, gjenkjenner ansikt og sjekker liveness (RGB, flere bilder), og publiserer resultatet på MQTT. Modul B er en
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

### Arkitektur: tre tjenester, tre images
| Tjeneste | Ansvar | Kjenner til |
|---|---|---|
| `FaceRecognizer.Recognition` (rot-`Dockerfile`) | Deteksjon, embedding, matching mot minneindeks, MQTT mot HA | Database (leser embeddings), ingen admin |
| `FaceRecognizer.Database` (`src/FaceRecognizer.Database/Dockerfile`) | Eier SQLite (personer, bilder, embeddings). REST + publiserer `EmbeddingsChanged` på MQTT | Ingenting om ansiktsmotor |
| `FaceRecognizer.Admin` (`src/FaceRecognizer.Admin/Dockerfile`) | Administrasjons-API. Sender bilde til Recognition (`/internal/embed`) og lagrer resultatet i Database | Begge via HTTP |

`FaceRecognizer.Contracts` har delte DTO-er. Tjenestene bruker delte bearer-tokens (`DATABASE_TOKEN`,
`RECOGNITION_API_TOKEN`, `ADMIN_TOKEN`); Database og Admin starter ikke uten token (Recognition: intern API er av).
Når Database endres publiseres `facerecognizer/embeddings/changed`, og Recognition laster indeksen på nytt atomisk.
Embeddings (normalisert, én per bilde) lagres med modellversjon; embeddings fra andre modeller ignoreres.
Kun Admin publiserer port (8080) mot LAN. Database og Recognition ligger på det interne nettet.
Database og Admin finnes også som HA-apper (`addons/database`, `addons/admin`); se «Installere på HAOS» under.

### Registrere personer
Ta bildene med kameraet (4 s nedtelling mellom hvert bilde, 5-8 bilder anbefales). `enroll` sender bildene til
Admin-API-et (`Admin:BaseUrl` og `Admin:Token`):

```
docker compose run --rm -e Admin__BaseUrl=http://admin:8080 -e Admin__Token=<ADMIN_TOKEN> recognition enroll <person-navn> [--count 6] [--ir]
```

Bilder uten nøyaktig ett ansikt avvises. `--ir` tar i tillegg 2 IR-bilder.
Migrering av eldre `faces/<navn>/*.jpg`: Database importerer mappen `Database:LegacyImportFolder` (Compose monterer
`./faces`) som bilder uten embedding; kjør deretter `POST /api/reindex` mot Admin for å generere embeddings.

### Administrasjons-API (Admin-tjenesten)
Alle kall krever `Authorization: Bearer <ADMIN_TOKEN>`.

| Kall | Funksjon |
|---|---|
| `GET /api/persons` | Personer med antall embeddings |
| `POST /api/persons` `{"name":"Ola"}` | Opprett person (returnerer GUID) |
| `POST /api/persons/{id}/images` (multipart, felt `files`) | Registrer flere ansiktsbilder |
| `DELETE /api/persons/{id}/embeddings/{embeddingId}` | Slett ett referansebilde og embedding |
| `DELETE /api/persons/{id}` | Slett person |
| `POST /api/reindex?rebuild=true` | Embedder bilder uten embedding for gjeldende modell (`rebuild`: alle) |
| `GET /api/metrics` | Tid for deteksjon, embedding og matching |

Resultatet på MQTT inneholder i tillegg `personId` (GUID) for gjenkjente personer, og `personEntityId`
(f.eks. `person.daniel`) hvis personen er koblet til en Home Assistant person-entitet i Admin. Automasjoner kan da
kreve at telefonen er hjemme: `{{ is_state(value_json.personEntityId, 'home') }}`. Uten kobling er `personEntityId`
`null`, og slike treff bør ikke åpne dør.

## Installere på HAOS (fire apper fra dette repoet)
Alt gjøres i HA-nettgrensesnittet. Appene hentes direkte fra GitHub (repoet må være offentlig), og ingenting bygges på HAOS-maskinen for Database, Admin og Recognition.

1. **Publiser imagene (én gang per versjon):** `git tag v0.1.0 && git push origin v0.1.0`. Workflowen `publish-addons.yml` bygger og pusher
   `ghcr.io/da9el-pettersen/facerecognizer-recognition`, `-database` og `-admin`. Taggen må være lik `version` i `config.yaml`-filene.
   Sjekk at pakkene er offentlige (GitHub → Packages → Package settings → Visibility).
2. **Legg til repoet:** Innstillinger → Apper → Appbutikk → ⋮ → Egendefinerte repoer → `https://github.com/da9el-pettersen/FaceRecognizer`.
   Installer «DeepFace», «Face Database», «Face Recognizer» og «Face Admin».
3. **Lag tre tokens** (ulike verdier) og fyll inn:

| App | Innstilling | Verdi |
|---|---|---|
| DeepFace | `auth_token` | `DEEPFACE_AUTH_TOKEN` |
| Face Database | `database.token` | `DATABASE_TOKEN` |
| Face Recognizer | `deepface.auth_token` / `database.token` / `api.token` | `DEEPFACE_AUTH_TOKEN` / `DATABASE_TOKEN` / `RECOGNITION_API_TOKEN` |
| Face Admin | `database.token` / `recognition.token` | `DATABASE_TOKEN` / `RECOGNITION_API_TOKEN` |

   Recognition trenger i tillegg `camera.*` (adresse, bruker, passord og `certificate_sha256`).
4. **Kontroller hostnavn:** appene finner hverandre på `def5d25e-deepface`, `def5d25e-face-database` og `def5d25e-face-recognizer`
   (`def5d25e` er en hash av repo-adressen). Hostnavnet står på hver apps infoside; avviker det, rett `base_url` i innstillingene.
5. **Start-rekkefølge:** Mosquitto, Face Database, DeepFace, Face Recognizer, Face Admin. Recognition prøver på nytt hvert 10. sekund til Database svarer.
6. **Bruk Admin:** «Face Admin» dukker opp i HA-sidepanelet (ingress). HA-brukeren stoles på (kun når kallet kommer fra Supervisor `172.30.32.2` med `X-Remote-User-Id`), så ingen ekstra innlogging. Sett `admin.allowed_users` (HA-brukernavn eller bruker-ID, kommaseparert) for å begrense hvem som slippes inn; tom = alle innloggede HA-brukere (`panel_admin: true` skjuler i tillegg panelet for ikke-administratorer, men håndhever ikke). Endringer logges med brukernavn. Direkte kall (port 8080 på LAN, `enroll`, skript) krever `Authorization: Bearer <ADMIN_TOKEN>`; `admin.token` er valgfritt når du bare bruker ingress.
7. **Eldre bilder:** legg dem i Face Database sin konfigurasjonsmappe (`/addon_configs/<slug>_face_database/faces/<navn>/`), sett
   `database.legacy_import_folder` til `/config/faces`, start om, og kjør `POST /api/reindex`.

Databasen ligger i appens `/data` og tas med i HA-backup.
## Lokal test (Docker Compose, uten HAOS)
```powershell
Copy-Item .env.example .env        # fyll inn DEEPFACE_AUTH_TOKEN, DATABASE_TOKEN, RECOGNITION_API_TOKEN, ADMIN_TOKEN, CAMERA_*; .env ligger i .gitignore
docker compose --profile dev up -d --build   # dev = Mosquitto-testbroker på internt nett
```
Nettverk: `internal` (`internal: true`, ingen vei ut) har DeepFace, Database og Mosquitto; `recognition` er også på `lan` for å nå
kameraet, og `admin` for å publisere port 8080.

### 1. Test kamerakontakt (anbefalt første steg)
1. Finn kameraets sertifikat-fingeravtrykk (kun TLS-handshake, ingen innlogging):
   ```powershell
   $kamera='<kamera-adresse>'; $c=New-Object Net.Sockets.TcpClient($kamera,443); $s=New-Object Net.Security.SslStream($c.GetStream(),$false,({$true}))
   $s.AuthenticateAsClient($kamera); ([Security.Cryptography.X509Certificates.X509Certificate2]$s.RemoteCertificate).GetCertHashString('SHA256')
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
HAOS kjører ikke Compose. Bruk appene fra dette repoet (se «Installere på HAOS»):
1. «DeepFace» (vert `def5d25e-deepface`, port 5000 kun internt). Sett `auth_token`.
2. «Face Recognizer». Sett `face.provider: DeepFace`,
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
- `Liveness:MinScore` (standard 0.8) og `Liveness:MinLiveFraction` (0.6): median og andel bilder over terskel i sekvensen.
- Modellscore er ikke kalibrert sannsynlighet. Mål falske godkjenninger/avvisninger med egne bilder, utskrift og skjerm.

## Video og liveness (RTSP)
Én ffmpeg-prosess leser kameraets RTSP-strøm (`Camera:Stream` = `main`/`sub`, ca. `Stream:Fps` 5 bilder/s) inn i en
ringbuffer. Ved trigger analyseres siste `Pipeline:Window` (3 s, minst `MinFrames` 8) – gjenkjenning og liveness
bruker **samme bilder**. Resultatet har `trackId`, `frameFrom`/`frameTo` og `windowStart`/`windowEnd`.
- ffmpeg må finnes (er med i imaget; lokalt `Stream:FfmpegPath`). `Camera:RtspUrl` overstyrer URL (ellers
  `rtsp://bruker:passord@vert:554/Preview_01_main`). Tilkobling gjenopprettes automatisk; ved reconnect startes en ny
  økt, og gamle/gjentatte bilder kan aldri inngå i en sekvens (`stale_frames`, `too_few_frames`).
- Ansiktet spores gjennom sekvensen (IoU) og identiteten må være samstemt på flere bilder, ellers avvises sekvensen
  (`identity_changed`, `ambiguous_tracking`, `track_unstable`).
- Bildetype (RGB/IR) bestemmes av kromatisk spredning pluss kameraets konfigurerte dag/natt-modus. Reolinks HTTP-API
  gir kun *konfigurert* modus, ikke faktisk tilstand i Auto (det krever Baichuan, port 9000, ikke implementert).
- MiniFASNet (V2 + V1SE) kjører kun på RGB. IR/ukjent gir `livenessUnavailable` (aldri `verified`) så lenge ingen
  IR-modell er registrert (`ILivenessModel.Supports`). `Liveness:Required=true` er standard.
- Statusfelt (nye, valgfrie): `recognitionStatus` (`faceRecognized|unknown|noFace|notAttempted|identityConflict`),
  `livenessStatus` (`livenessPassed|livenessFailed|livenessUnavailable|notRequired|notEvaluated`), `imageMode`, `reason`.
  `outcome` kan nå også være `livenessUnavailable`; øvrige felt er uendret.
- Kjent begrensning: helt identiske påfølgende bilder (statisk scene) forkastes som duplikater.
- ffmpeg kjøres uten `-fflags nobuffer`/`-flags low_delay`: med Reolink-kameraet ga disse HEVC-dekodefeil og tapte bilder.
- Mål ressursbruk mot ditt kamera: `dotnet FaceRecognizer.Recognition.dll streamtest --seconds 30 [--stream main|sub]`.
- Test hele pipelinen på ekte video: `livetest --runs N [--stream main|sub] [--csv <absolutt sti>]`. Med
  `--enroll Navn --enroll-count 8` registreres en testperson kun i minnet (FaceAiSharp), og
  `--phases "ekte:6,skjerm_foto:10" --gate <mappe>` pauser før hvert scenario til filen `go` opprettes i mappen.
  Lokal kameraoppsett ligger i `camera.local.env` (ikke committet, se `camera.local.env.example`).
- `Face:Threshold` er motorspesifikk: standard 0.62 (FaceAiSharp; var 0.98, urealistisk for live video). Typiske score for samme person er 0.65–0.95; kalibrer med egne data.
- Flerbildeanalyse øker stabilitet, men beskytter ikke alene mot video-replay.
