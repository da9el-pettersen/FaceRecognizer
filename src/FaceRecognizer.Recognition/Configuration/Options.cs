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

    /// <summary>Adresse til kameraet, f.eks. https://kamera.local.</summary>
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
    /// <summary>Minste tid mellom to behandlinger, for å unngå gjentatte triggere.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(10);
}
