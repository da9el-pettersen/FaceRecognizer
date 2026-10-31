namespace FaceRecognizer.Recognition.Configuration;

/// <summary>Innstillinger for MQTT-brokeren (Mosquitto i HAOS).</summary>
public sealed class MqttOptions
{
    /// <summary>Vertsnavn på brokeren.</summary>
    public string Host { get; set; } = "core-mosquitto";

    /// <summary>Port på brokeren.</summary>
    public int Port { get; set; } = 1883;

    /// <summary>Brukernavn til brokeren.</summary>
    public string? Username { get; set; }

    /// <summary>Passord til brokeren.</summary>
    public string? Password { get; set; }

    /// <summary>Om TLS skal brukes mot brokeren.</summary>
    public bool UseTls { get; set; }

    /// <summary>Topic Home Assistant publiserer til når kameraet oppdager en person.</summary>
    public string TriggerTopic { get; set; } = "facerecognizer/trigger";

    /// <summary>Topic som resultatet publiseres på.</summary>
    public string ResultTopic { get; set; } = Contracts.RecognitionResult.Topic;

    /// <summary>Topic for tilgjengelighet (online/offline).</summary>
    public string StatusTopic { get; set; } = "facerecognizer/status";

    /// <summary>Prefiks for MQTT Discovery i Home Assistant.</summary>
    public string DiscoveryPrefix { get; set; } = "homeassistant";
}

/// <summary>Innstillinger for Reolink-kameraet.</summary>
public sealed class CameraOptions
{
    /// <summary>Navn på kameraet, tas med i publiserte resultater.</summary>
    public string Name { get; set; } = "reolink";

    /// <summary>Adresse til kameraet, f.eks. https://<kamera-adresse>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Kamerabruker.</summary>
    public string Username { get; set; } = "";

    /// <summary>Kamerapassord.</summary>
    public string Password { get; set; } = "";

    /// <summary>SHA-256 av kameraets sertifikat (hex). Kall mot kameraet avvises om det ikke stemmer.</summary>
    public string CertificateSha256 { get; set; } = "";

    /// <summary>Kanalnummer på kameraet.</summary>
    public int Channel { get; set; }

    /// <summary>Ventetid etter bytte til IR før bilde tas.</summary>
    public TimeSpan IrSettleDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Full RTSP-adresse. Tomt = bygges fra <see cref="BaseUrl"/>, bruker, passord, kanal og <see cref="Stream"/>.</summary>
    public string RtspUrl { get; set; } = "";

    /// <summary>Hvilken strøm som brukes når RTSP-adressen bygges: <c>main</c> (best kvalitet) eller <c>sub</c> (lav CPU).</summary>
    public string Stream { get; set; } = "main";
}

/// <summary>Innstillinger for den delte videostrømmen (RTSP via ffmpeg) og bildebufferen.</summary>
public sealed class StreamOptions
{
    /// <summary>Analysefrekvens (bilder per sekund). Strømmen reduseres til dette i ffmpeg.</summary>
    public double Fps { get; set; } = 5;

    /// <summary>Største bredde på bildene som analyseres. Større bilder skaleres ned i ffmpeg.</summary>
    public int MaxWidth { get; set; } = 1280;

    /// <summary>Antall bilder bufferen holder.</summary>
    public int BufferFrames { get; set; } = 20;

    /// <summary>Sti til ffmpeg.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Hvis ingen nye bilder kommer innen dette, regnes strømmen som død og kobles til på nytt.</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Lengste ventetid mellom reconnect-forsøk (eksponentiell økning).</summary>
    public TimeSpan ReconnectMaxBackoff { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Innstillinger for det interne API-et som Admin bruker til å lage embeddings.</summary>
public sealed class InternalApiOptions
{
    /// <summary>Delt hemmelighet. Tomt = API-et er slått av. Sendes som <c>Authorization: Bearer</c>.</summary>
    public string Token { get; set; } = "";

    /// <summary>Adresse API-et lytter på. Standard er kun lokalt; bruk <c>http://+:8099</c> for å nå det fra andre containere.</summary>
    public string Url { get; set; } = "http://127.0.0.1:8099";
}

/// <summary>Innstillinger for tilkobling til databasetjenesten (kun lesing av embeddings).</summary>
public sealed class DatabaseOptions
{
    /// <summary>Intern adresse til databasetjenesten.</summary>
    public string BaseUrl { get; set; } = "http://database:8080";

    /// <summary>Delt hemmelighet for databasetjenesten.</summary>
    public string Token { get; set; } = "";
}

/// <summary>Innstillinger for selve pipelinen.</summary>
public sealed class PipelineOptions
{
    /// <summary>Minste tid mellom to behandlinger. Standard er ingen pause: tjenesten tar neste bilde så fort den er ledig.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.Zero;

    /// <summary>Lengden på bildesekvensen som analyseres per trigger.</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Minste antall bilder i sekvensen. Færre gir <c>error</c> (døren forblir låst).</summary>
    public int MinFrames { get; set; } = 8;

    /// <summary>Eldste tillatte alder på siste bilde. Eldre bilder brukes aldri som ny sekvens.</summary>
    public TimeSpan MaxFrameAge { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Største tillatte hull mellom to bilder i sekvensen.</summary>
    public TimeSpan MaxFrameGap { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Bredden bildene skaleres ned til for ansiktsdeteksjon. Gjenkjenning og liveness bruker full analyseoppløsning.</summary>
    public int DetectionWidth { get; set; } = 640;

    /// <summary>Minste ansiktsbredde (piksler i analysebildet) for å gjenkjenne og vurdere liveness.</summary>
    public int MinFaceWidth { get; set; } = 60;

    /// <summary>Antall bilder i sporet som gjenkjennes hver for seg for å bekrefte at det er samme person.</summary>
    public int RecognitionFrames { get; set; } = 3;

    /// <summary>Minste antall av disse bildene som må peke på samme person.</summary>
    public int IdentityMinAgreement { get; set; } = 2;

    /// <summary>Minste andel av disse bildene som må peke på samme person.</summary>
    public float IdentityMinFraction { get; set; } = 0.67f;

    /// <summary>Minste overlapp (IoU) for å koble et ansikt til samme spor mellom to bilder.</summary>
    public float TrackMinIou { get; set; } = 0.3f;

    /// <summary>
    /// Om ansiktsgjenkjenning kjøres på IR/svart-hvitt-bilder. Av som standard fordi motoren ikke er validert for IR.
    /// Liveness er uansett utilgjengelig i IR uten egnet modell, så resultatet blir aldri <c>verified</c> når liveness kreves.
    /// </summary>
    public bool InfraredRecognition { get; set; }
}
