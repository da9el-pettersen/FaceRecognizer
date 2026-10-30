using System.Runtime.InteropServices;
using System.Security.Cryptography;
using FaceRecognizer.Contracts;
using Microsoft.Data.Sqlite;

namespace FaceRecognizer.Database.Storage;

/// <summary>
/// Varig lagring av personer, referansebilder og embeddings i SQLite. Hver embedding er merket med modellversjon
/// og dimensjon. Bildene lagres slik at embeddings kan lages på nytt når modellen byttes.
/// </summary>
public sealed class EmbeddingStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    /// <summary>Oppretter lageret. Databasefilen opprettes ved første bruk.</summary>
    /// <param name="databasePath">Sti til SQLite-filen.</param>
    public EmbeddingStore(string databasePath)
    {
        // Ingen tilkoblingspool, slik at filen ikke holdes låst mellom operasjoner.
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
    }

    /// <summary>Oppretter en person.</summary>
    /// <param name="name">Navn.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns>Personen, eller <c>null</c> hvis navnet allerede er i bruk.</returns>
    public async Task<PersonDto?> TryCreatePersonAsync(string name, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await FindPersonByNameAsync(connection, name, cancellationToken) is null
            ? await InsertPersonAsync(connection, name, cancellationToken)
            : null;
    }

    /// <summary>Henter en person.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<PersonDto?> GetPersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        return await GetPersonAsync(connection, personId, cancellationToken);
    }

    /// <summary>Lister alle personer med antall embeddings for en modellversjon.</summary>
    /// <param name="modelId">Modellversjon det telles for.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<IReadOnlyList<PersonSummaryDto>> ListPersonsAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id, p.name, COUNT(e.id), p.ha_person_entity
            FROM persons p
            LEFT JOIN images i ON i.person_id = p.id
            LEFT JOIN embeddings e ON e.image_id = i.id AND e.model_id = $model
            GROUP BY p.id, p.name, p.ha_person_entity ORDER BY p.name
            """;
        command.Parameters.AddWithValue("$model", modelId);
        var result = new List<PersonSummaryDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PersonSummaryDto(
                Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return result;
    }

    /// <summary>Setter eller fjerner koblingen til en Home Assistant person-entitet.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="entityId">Entitets-ID, eller <c>null</c> for å fjerne koblingen.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns><c>false</c> hvis personen ikke finnes.</returns>
    public async Task<bool> SetPersonEntityAsync(Guid personId, string? entityId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE persons SET ha_person_entity = $entity WHERE id = $id";
        command.Parameters.AddWithValue("$entity", (object?)entityId ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", personId.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Sletter en person med alle bilder og embeddings.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<bool> DeletePersonAsync(Guid personId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM persons WHERE id = $id";
        command.Parameters.AddWithValue("$id", personId.ToString());
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    /// <summary>Lagrer et referansebilde og embeddingen for en modell.</summary>
    /// <param name="personId">Eier.</param>
    /// <param name="image">Bildedata.</param>
    /// <param name="modelId">Modellversjon.</param>
    /// <param name="embedding">Normalisert embedding.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns>
    /// <see cref="AddImageStatus.Added"/> med IDene, <see cref="AddImageStatus.PersonNotFound"/>, eller
    /// <see cref="AddImageStatus.Duplicate"/> hvis bildet allerede har embedding for modellen.
    /// </returns>
    public async Task<(AddImageStatus Status, AddReferenceImageResponse? Result)> AddImageAsync(
        Guid personId, byte[] image, string modelId, float[] embedding, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        if (await GetPersonAsync(connection, personId, cancellationToken) is null)
        {
            return (AddImageStatus.PersonNotFound, null);
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var imageId = await FindOrInsertImageAsync(connection, transaction, personId, image, cancellationToken);
        var embeddingId = await InsertEmbeddingAsync(
            connection, transaction, imageId, modelId, embedding, replace: false, cancellationToken);
        if (embeddingId is null)
        {
            return (AddImageStatus.Duplicate, null);
        }

        await transaction.CommitAsync(cancellationToken);
        return (AddImageStatus.Added, new AddReferenceImageResponse(imageId, embeddingId.Value));
    }

    /// <summary>
    /// Lagrer et bilde uten embedding (import av eldre referansebilder). Hopper over bildet hvis personen har det fra før.
    /// </summary>
    /// <param name="personName">Personens navn. Personen opprettes ved behov.</param>
    /// <param name="image">Bildedata.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns><c>true</c> hvis bildet var nytt.</returns>
    public async Task<bool> ImportImageAsync(string personName, byte[] image, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var person = await FindPersonByNameAsync(connection, personName, cancellationToken)
            ?? await InsertPersonAsync(connection, personName, cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var before = await CountImagesAsync(connection, transaction, cancellationToken);
        await FindOrInsertImageAsync(connection, transaction, person.PersonId, image, cancellationToken);
        var added = await CountImagesAsync(connection, transaction, cancellationToken) > before;
        await transaction.CommitAsync(cancellationToken);
        return added;
    }

    /// <summary>Lister referansebilder.</summary>
    /// <param name="modelId">Modellversjon det filtreres på.</param>
    /// <param name="missingOnly">Bare bilder som mangler embedding for <paramref name="modelId"/>.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<IReadOnlyList<ReferenceImageDto>> ListImagesAsync(
        string modelId, bool missingOnly, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT i.id, p.id, p.name FROM images i JOIN persons p ON p.id = i.person_id
            WHERE $all = 1 OR NOT EXISTS (SELECT 1 FROM embeddings e WHERE e.image_id = i.id AND e.model_id = $model)
            ORDER BY i.id
            """;
        command.Parameters.AddWithValue("$all", missingOnly ? 0 : 1);
        command.Parameters.AddWithValue("$model", modelId);
        var result = new List<ReferenceImageDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ReferenceImageDto(reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2)));
        }

        return result;
    }

    /// <summary>Henter bildedata.</summary>
    /// <param name="imageId">Bilde-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<byte[]?> GetImageDataAsync(long imageId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM images WHERE id = $id";
        command.Parameters.AddWithValue("$id", imageId);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    /// <summary>Oppretter eller erstatter embeddingen for et bilde og en modell.</summary>
    /// <param name="imageId">Bilde-ID.</param>
    /// <param name="modelId">Modellversjon.</param>
    /// <param name="embedding">Normalisert embedding.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    /// <returns><c>false</c> hvis bildet ikke finnes.</returns>
    public async Task<bool> SetEmbeddingAsync(
        long imageId, string modelId, float[] embedding, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT 1 FROM images WHERE id = $id";
        exists.Parameters.AddWithValue("$id", imageId);
        if (await exists.ExecuteScalarAsync(cancellationToken) is null)
        {
            return false;
        }

        await InsertEmbeddingAsync(connection, null, imageId, modelId, embedding, replace: true, cancellationToken);
        return true;
    }

    /// <summary>Lister embeddings for en person og modell.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="modelId">Modellversjon.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<IReadOnlyList<PersonEmbeddingDto>> ListPersonEmbeddingsAsync(
        Guid personId, string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, e.image_id FROM embeddings e JOIN images i ON i.id = e.image_id
            WHERE i.person_id = $person AND e.model_id = $model ORDER BY e.id
            """;
        command.Parameters.AddWithValue("$person", personId.ToString());
        command.Parameters.AddWithValue("$model", modelId);
        var result = new List<PersonEmbeddingDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PersonEmbeddingDto(reader.GetInt64(0), reader.GetInt64(1)));
        }

        return result;
    }

    /// <summary>Laster alle embeddings for en modellversjon. Rader med feil størrelse hoppes over.</summary>
    /// <param name="modelId">Modellversjon.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<EmbeddingSnapshotDto> LoadSnapshotAsync(string modelId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id, p.id, p.name, e.dimension, e.vector, p.ha_person_entity
            FROM embeddings e JOIN images i ON i.id = e.image_id JOIN persons p ON p.id = i.person_id
            WHERE e.model_id = $model ORDER BY e.id
            """;
        command.Parameters.AddWithValue("$model", modelId);
        var result = new List<EmbeddingDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var blob = (byte[])reader.GetValue(4);
                var dimension = reader.GetInt32(3);
                if (dimension > 0 && blob.Length == dimension * sizeof(float))
                {
                    result.Add(new EmbeddingDto(
                        reader.GetInt64(0), Guid.Parse(reader.GetString(1)), reader.GetString(2),
                        MemoryMarshal.Cast<byte, float>(blob).ToArray(), reader.IsDBNull(5) ? null : reader.GetString(5)));
                }
            }
        }

        await using var other = connection.CreateCommand();
        other.CommandText = "SELECT COUNT(*) FROM embeddings WHERE model_id <> $model";
        other.Parameters.AddWithValue("$model", modelId);
        return new EmbeddingSnapshotDto(result, Convert.ToInt32(await other.ExecuteScalarAsync(cancellationToken)));
    }

    /// <summary>Sletter en embedding. Bildet slettes også når det ikke har flere embeddings.</summary>
    /// <param name="embeddingId">Embedding-ID.</param>
    /// <param name="cancellationToken">Avbruddstoken.</param>
    public async Task<bool> DeleteEmbeddingAsync(long embeddingId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var lookup = connection.CreateCommand();
        lookup.CommandText = "SELECT image_id FROM embeddings WHERE id = $id";
        lookup.Parameters.AddWithValue("$id", embeddingId);
        if (await lookup.ExecuteScalarAsync(cancellationToken) is not long imageId)
        {
            return false;
        }

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using var delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM embeddings WHERE id = $id;
            DELETE FROM images WHERE id = $image AND NOT EXISTS (SELECT 1 FROM embeddings WHERE image_id = $image);
            """;
        delete.Parameters.AddWithValue("$id", embeddingId);
        delete.Parameters.AddWithValue("$image", imageId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<long> FindOrInsertImageAsync(
        SqliteConnection connection, SqliteTransaction transaction, Guid personId, byte[] image,
        CancellationToken cancellationToken)
    {
        var hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO images (person_id, hash, data, created_at) VALUES ($person, $hash, $data, $created);
            SELECT id FROM images WHERE person_id = $person AND hash = $hash;
            """;
        command.Parameters.AddWithValue("$person", personId.ToString());
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$data", image);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<long?> InsertEmbeddingAsync(
        SqliteConnection connection, SqliteTransaction? transaction, long imageId, string modelId, float[] embedding,
        bool replace, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT OR {(replace ? "REPLACE" : "IGNORE")} INTO embeddings (image_id, model_id, dimension, vector, created_at)
            VALUES ($image, $model, $dim, $vector, $created);
            SELECT changes() > 0, last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$image", imageId);
        command.Parameters.AddWithValue("$model", modelId);
        command.Parameters.AddWithValue("$dim", embedding.Length);
        command.Parameters.AddWithValue("$vector", MemoryMarshal.AsBytes<float>(embedding).ToArray());
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetInt64(0) == 1 ? reader.GetInt64(1) : null;
    }

    private static async Task<int> CountImagesAsync(
        SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM images";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<PersonDto?> GetPersonAsync(
        SqliteConnection connection, Guid personId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, ha_person_entity FROM persons WHERE id = $id";
        command.Parameters.AddWithValue("$id", personId.ToString());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPerson(reader) : null;
    }

    private static PersonDto ReadPerson(SqliteDataReader reader) =>
        new(Guid.Parse(reader.GetString(0)), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));

    private static async Task<PersonDto?> FindPersonByNameAsync(
        SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, ha_person_entity FROM persons WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadPerson(reader) : null;
    }

    private static async Task<PersonDto> InsertPersonAsync(
        SqliteConnection connection, string name, CancellationToken cancellationToken)
    {
        var person = new PersonDto(Guid.NewGuid(), name);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO persons (id, name, created_at) VALUES ($id, $name, $created)";
        command.Parameters.AddWithValue("$id", person.PersonId.ToString());
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return person;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            var path = new SqliteConnectionStringBuilder(_connectionString).DataSource;
            var directory = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                CREATE TABLE IF NOT EXISTS persons (
                    id TEXT PRIMARY KEY,
                    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                    created_at TEXT NOT NULL);
                CREATE TABLE IF NOT EXISTS images (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    person_id TEXT NOT NULL REFERENCES persons(id) ON DELETE CASCADE,
                    hash TEXT NOT NULL,
                    data BLOB NOT NULL,
                    created_at TEXT NOT NULL,
                    UNIQUE (person_id, hash));
                CREATE TABLE IF NOT EXISTS embeddings (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
                    model_id TEXT NOT NULL,
                    dimension INTEGER NOT NULL,
                    vector BLOB NOT NULL,
                    created_at TEXT NOT NULL,
                    UNIQUE (image_id, model_id));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            // Migrering: koblingen til Home Assistant person-entitet ble lagt til etter første utgivelse.
            await using var columns = connection.CreateCommand();
            columns.CommandText = "SELECT COUNT(*) FROM pragma_table_info('persons') WHERE name = 'ha_person_entity'";
            if (Convert.ToInt32(await columns.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                await using var migrate = connection.CreateCommand();
                migrate.CommandText = "ALTER TABLE persons ADD COLUMN ha_person_entity TEXT";
                await migrate.ExecuteNonQueryAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }
}

/// <summary>Utfall av <see cref="EmbeddingStore.AddImageAsync"/>.</summary>
public enum AddImageStatus
{
    /// <summary>Bilde og embedding ble lagret.</summary>
    Added,

    /// <summary>Personen finnes ikke.</summary>
    PersonNotFound,

    /// <summary>Bildet har allerede embedding for modellen.</summary>
    Duplicate
}
