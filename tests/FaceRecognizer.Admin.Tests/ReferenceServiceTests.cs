using FaceRecognizer.Admin;
using FaceRecognizer.Admin.Clients;
using FaceRecognizer.Contracts;

namespace FaceRecognizer.Admin.Tests;

/// <summary>Tester for Admin sin logikk med falske klienter, uten AI-modell og uten database.</summary>
public sealed class ReferenceServiceTests
{
    private const string Model = "modell-v1";
    private readonly FakeRecognition _recognition = new();
    private readonly FakeDatabase _database = new();

    private ReferenceService CreateService() => new(_database, _recognition);

    [Fact]
    public async Task Bilde_med_ett_ansikt_embeddes_og_lagres_i_databasen()
    {
        var person = _database.AddPerson("Ola");
        _recognition.Faces[1] = [[1f, 0f]];

        var results = await CreateService().AddImagesAsync(person.PersonId, [("a.jpg", [1])], default);

        var result = Assert.Single(results);
        Assert.Equal("added", result.Status);
        Assert.NotNull(result.EmbeddingId);
        Assert.Equal(Model, Assert.Single(_database.Added).ModelId);
    }

    [Fact]
    public async Task Bilder_uten_noyaktig_ett_ansikt_avvises_uten_lagring()
    {
        var person = _database.AddPerson("Ola");
        _recognition.Faces[1] = [];
        _recognition.Faces[2] = [[1f, 0f], [0f, 1f]];

        var results = await CreateService().AddImagesAsync(
            person.PersonId, [("ingen.jpg", [1]), ("flere.jpg", [2]), ("ulesbar.jpg", [3])], default);

        Assert.Equal(["rejected", "rejected", "invalid"], results.Select(r => r.Status));
        Assert.Empty(_database.Added);
    }

    [Fact]
    public async Task Identisk_bilde_rapporteres_som_duplicate()
    {
        var person = _database.AddPerson("Ola");
        _recognition.Faces[1] = [[1f, 0f]];
        _database.DuplicateOnAdd = true;

        var result = Assert.Single(await CreateService().AddImagesAsync(person.PersonId, [("a.jpg", [1])], default));

        Assert.Equal("duplicate", result.Status);
    }

    [Fact]
    public async Task Ukjent_person_gir_KeyNotFoundException()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => CreateService().AddImagesAsync(Guid.NewGuid(), [("a.jpg", [1])], default));
    }

    [Fact]
    public async Task Sletting_krever_at_embeddingen_tilhorer_personen()
    {
        var ola = _database.AddPerson("Ola");
        var kari = _database.AddPerson("Kari");
        _database.PersonEmbeddings[ola.PersonId] = [new PersonEmbeddingDto(10, 100)];

        Assert.False(await CreateService().DeleteEmbeddingAsync(kari.PersonId, 10, default));
        Assert.True(await CreateService().DeleteEmbeddingAsync(ola.PersonId, 10, default));
        Assert.Equal([10L], _database.DeletedEmbeddings);
    }

    [Fact]
    public async Task Reindeksering_embedder_bare_bilder_som_mangler_med_mindre_rebuild()
    {
        _recognition.Faces[7] = [[0f, 1f]];
        _recognition.Faces[8] = [];
        _database.Images.Add((new ReferenceImageDto(1, Guid.NewGuid(), "Ola"), [7], Missing: true));
        _database.Images.Add((new ReferenceImageDto(2, Guid.NewGuid(), "Kari"), [8], Missing: true));
        _database.Images.Add((new ReferenceImageDto(3, Guid.NewGuid(), "Per"), [7], Missing: false));

        var missingOnly = await CreateService().ReindexAsync(rebuild: false, default);
        var rebuild = await CreateService().ReindexAsync(rebuild: true, default);

        Assert.Equal(new ReindexResult(1, 1), missingOnly);
        Assert.Equal(new ReindexResult(2, 1), rebuild);
        Assert.All(_database.SetEmbeddings, s => Assert.Equal(Model, s.ModelId));
    }

    private sealed class FakeRecognition : IRecognitionClient
    {
        // Bildets første byte velger hvilke ansikter som "finnes". Manglende nøkkel = bildet kan ikke leses.
        public Dictionary<byte, float[][]> Faces { get; } = [];

        public Task<ModelInfoDto> GetModelAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ModelInfoDto(Model));

        public Task<EmbedResponse?> EmbedAsync(byte[] image, CancellationToken cancellationToken) =>
            Task.FromResult(Faces.TryGetValue(image[0], out var faces) ? new EmbedResponse(Model, faces) : null);

        public Task<System.Text.Json.JsonElement> GetMetricsAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeDatabase : IDatabaseClient
    {
        private readonly List<PersonDto> _persons = [];

        public List<AddReferenceImageRequest> Added { get; } = [];
        public bool DuplicateOnAdd { get; set; }
        public Dictionary<Guid, List<PersonEmbeddingDto>> PersonEmbeddings { get; } = [];
        public List<long> DeletedEmbeddings { get; } = [];
        public List<(ReferenceImageDto Image, byte[] Data, bool Missing)> Images { get; } = [];
        public List<(long ImageId, string ModelId)> SetEmbeddings { get; } = [];

        public PersonDto AddPerson(string name)
        {
            var person = new PersonDto(Guid.NewGuid(), name);
            _persons.Add(person);
            return person;
        }

        public Task<IReadOnlyList<PersonSummaryDto>> ListPersonsAsync(string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersonSummaryDto>>(
                _persons.Select(p => new PersonSummaryDto(p.PersonId, p.Name, 0)).ToList());

        public Task<PersonDto?> CreatePersonAsync(string name, CancellationToken cancellationToken) =>
            Task.FromResult<PersonDto?>(AddPerson(name));

        public Task<PersonDto?> GetPersonAsync(Guid personId, CancellationToken cancellationToken) =>
            Task.FromResult(_persons.FirstOrDefault(p => p.PersonId == personId));

        public Task<bool> DeletePersonAsync(Guid personId, CancellationToken cancellationToken) =>
            Task.FromResult(_persons.RemoveAll(p => p.PersonId == personId) > 0);

        public Task<bool> SetPersonEntityAsync(Guid personId, string? entityId, CancellationToken cancellationToken)
        {
            var index = _persons.FindIndex(p => p.PersonId == personId);
            if (index >= 0)
            {
                _persons[index] = _persons[index] with { HaPersonEntity = entityId };
            }

            return Task.FromResult(index >= 0);
        }

        public Task<IReadOnlyList<PersonEmbeddingDto>> ListPersonEmbeddingsAsync(
            Guid personId, string modelId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PersonEmbeddingDto>>(PersonEmbeddings.GetValueOrDefault(personId, []));

        public Task<AddReferenceImageResponse?> AddImageAsync(
            Guid personId, AddReferenceImageRequest request, CancellationToken cancellationToken)
        {
            if (DuplicateOnAdd)
            {
                return Task.FromResult<AddReferenceImageResponse?>(null);
            }

            Added.Add(request);
            return Task.FromResult<AddReferenceImageResponse?>(new AddReferenceImageResponse(Added.Count, Added.Count));
        }

        public Task<IReadOnlyList<ReferenceImageDto>> ListImagesAsync(
            string modelId, bool missingOnly, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ReferenceImageDto>>(
                Images.Where(i => !missingOnly || i.Missing).Select(i => i.Image).ToList());

        public Task<byte[]?> GetImageDataAsync(long imageId, CancellationToken cancellationToken) =>
            Task.FromResult<byte[]?>(Images.First(i => i.Image.ImageId == imageId).Data);

        public Task<bool> SetEmbeddingAsync(
            long imageId, string modelId, float[] embedding, CancellationToken cancellationToken)
        {
            SetEmbeddings.Add((imageId, modelId));
            return Task.FromResult(true);
        }

        public Task<bool> DeleteEmbeddingAsync(long embeddingId, CancellationToken cancellationToken)
        {
            DeletedEmbeddings.Add(embeddingId);
            return Task.FromResult(true);
        }
    }
}
