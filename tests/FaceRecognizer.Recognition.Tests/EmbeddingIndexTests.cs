using FaceRecognizer.Contracts;
using FaceRecognizer.Recognition.Engines;

namespace FaceRecognizer.Recognition.Tests;

/// <summary>Tester for normalisering, cosinus-likhet, minneindeks og modellkompatibilitet med syntetiske vektorer.</summary>
public sealed class EmbeddingIndexTests
{
    private const string Model = "test-model-v1";

    private static float[] Unit(params float[] values)
    {
        EmbeddingMath.Normalize(values);
        return values;
    }

    private static FaceReferenceService CreateService(FakeEmbeddingSource? source = null, string modelId = Model) =>
        new(modelId, source ?? new FakeEmbeddingSource());

    /// <summary>Embeddingkilde i minnet som skiller mellom modellversjoner.</summary>
    private sealed class FakeEmbeddingSource : IEmbeddingSource
    {
        private readonly List<(string Model, EmbeddingDto Item)> _items = [];
        public int Loads { get; private set; }

        public void Add(string model, long id, Guid personId, string name, float[] embedding) =>
            _items.Add((model, new EmbeddingDto(id, personId, name, embedding)));

        public Task<EmbeddingSnapshotDto> LoadAsync(string modelId, CancellationToken cancellationToken)
        {
            Loads++;
            return Task.FromResult(new EmbeddingSnapshotDto(
                _items.Where(i => i.Model == modelId).Select(i => i.Item).ToList(),
                _items.Count(i => i.Model != modelId)));
        }
    }

    [Fact]
    public void Normalize_GivesUnitLength_AndRejectsZeroVector()
    {
        var vector = new float[] { 3, 4, 0, 0, 0, 0, 0, 0, 0, 12 };
        Assert.True(EmbeddingMath.Normalize(vector));
        Assert.Equal(1f, EmbeddingMath.Dot(vector, vector), 4);
        Assert.Equal(3f / 13f, vector[0], 5);

        var zero = new float[8];
        Assert.False(EmbeddingMath.Normalize(zero));
        Assert.False(EmbeddingMath.Normalize([float.NaN, 1f]));
    }

    [Fact]
    public void CosineSimilarity_HandlesParallelOrthogonalOppositeAndMismatch()
    {
        Assert.Equal(1f, EmbeddingMath.CosineSimilarity([1, 2, 3], [2, 4, 6]), 4);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity([1, 0], [0, 1]), 4);
        Assert.Equal(-1f, EmbeddingMath.CosineSimilarity([1, 2], [-1, -2]), 4);
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity([1, 2], [1, 2, 3]));
        Assert.Equal(0f, EmbeddingMath.CosineSimilarity([0, 0], [1, 2]));
    }

    [Fact]
    public void FindBest_PicksPersonWithHighestSimilarity()
    {
        var anne = Guid.NewGuid();
        var ola = Guid.NewGuid();
        var index = new EmbeddingIndex(Model);
        index.Add(anne, "Anne", 1, Unit(1, 0, 0));
        index.Add(ola, "Ola", 2, Unit(0, 1, 0));

        Assert.True(index.TryFindBest(Unit(0.1f, 1, 0), out var match));
        Assert.Equal(ola, match.PersonId);
        Assert.Equal("Ola", match.Name);
        Assert.True(match.Score > 0.99f);
    }

    [Fact]
    public void FindBest_UsesBestOfSeveralEmbeddingsPerPerson()
    {
        var anne = Guid.NewGuid();
        var ola = Guid.NewGuid();
        var index = new EmbeddingIndex(Model);
        index.Add(anne, "Anne", 1, Unit(1, 0, 0));
        index.Add(anne, "Anne", 2, Unit(0, 0, 1));
        index.Add(ola, "Ola", 3, Unit(0.7f, 0.7f, 0));

        Assert.True(index.TryFindBest(Unit(0, 0.1f, 1), out var match));
        Assert.Equal(anne, match.PersonId);
        Assert.Equal(3, index.EmbeddingCount);
        Assert.Equal(2, index.PersonCount);
    }

    [Fact]
    public void Match_BelowThreshold_ReturnsUnknown_AboveReturnsPerson()
    {
        var service = CreateService();
        var ola = Guid.NewGuid();
        service.Index.Add(ola, "Ola", 1, Unit(1, 0, 0));

        var unknown = service.Match(Unit(0, 1, 0), 0.5f);
        Assert.True(unknown.FaceFound);
        Assert.False(unknown.IsMatch);
        Assert.Null(unknown.PersonName);
        Assert.Null(unknown.PersonId);

        var known = service.Match(Unit(1, 0.1f, 0), 0.5f);
        Assert.True(known.IsMatch);
        Assert.Equal("Ola", known.PersonName);
        Assert.Equal(ola, known.PersonId);
    }

    [Fact]
    public void Match_returnerer_koblet_HA_entitet_kun_ved_treff()
    {
        var service = CreateService();
        service.Index.Add(Guid.NewGuid(), "Ola", 1, Unit(1, 0, 0), "person.ola");

        Assert.Equal("person.ola", service.Match(Unit(1, 0.1f, 0), 0.5f).PersonEntityId);
        Assert.Null(service.Match(Unit(0, 1, 0), 0.5f).PersonEntityId);
    }

    [Fact]
    public void Match_EmptyIndex_ReturnsUnknown()
    {
        var result = CreateService().Match(Unit(1, 0), 0.5f);
        Assert.False(result.IsMatch);
        Assert.Equal(0f, result.Score);
    }

    [Fact]
    public void Index_UpdatesWhenEmbeddingsAndPersonsAreAddedAndRemoved()
    {
        var anne = Guid.NewGuid();
        var ola = Guid.NewGuid();
        var index = new EmbeddingIndex(Model);
        index.Add(anne, "Anne", 1, Unit(1, 0));
        index.Add(anne, "Anne", 2, Unit(0, 1));
        index.Add(ola, "Ola", 3, Unit(1, 1));

        Assert.True(index.RemoveEmbedding(2));
        Assert.Equal(2, index.EmbeddingCount);
        Assert.False(index.RemoveEmbedding(2));

        Assert.True(index.RemoveEmbedding(1));
        Assert.Equal(1, index.PersonCount);

        Assert.True(index.RemovePerson(ola));
        Assert.False(index.TryFindBest(Unit(1, 0), out _));
    }

    [Fact]
    public void Index_RejectsWrongDimensionAndUnnormalizedVectors()
    {
        var index = new EmbeddingIndex(Model);
        index.Add(Guid.NewGuid(), "Anne", 1, Unit(1, 0, 0));

        Assert.Throws<ArgumentException>(() => index.Add(Guid.NewGuid(), "Ola", 2, Unit(1, 0)));
        Assert.Throws<ArgumentException>(() => index.Add(Guid.NewGuid(), "Ola", 3, [2f, 0f, 0f]));
    }

    [Fact]
    public async Task Index_SupportsConcurrentReadsDuringUpdates()
    {
        var index = new EmbeddingIndex(Model);
        var stable = Guid.NewGuid();
        index.Add(stable, "Stabil", 0, Unit(1, 0, 0, 0));
        var query = Unit(1, 0.01f, 0, 0);

        var writer = Task.Run(() =>
        {
            for (var i = 1; i <= 500; i++)
            {
                var id = Guid.NewGuid();
                index.Add(id, "Midlertidig", i, Unit(0, 1, i, 0));
                index.RemoveEmbedding(i);
            }
        });
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < 5000; i++)
            {
                Assert.True(index.TryFindBest(query, out var match));
                Assert.Equal(stable, match.PersonId);
            }
        }));

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(1, index.EmbeddingCount);
    }

    [Fact]
    public async Task Service_LoadsEmbeddingsFromSource_AndMatchesWithoutFurtherLookups()
    {
        var source = new FakeEmbeddingSource();
        var ola = Guid.NewGuid();
        source.Add(Model, 1, ola, "Ola", Unit(1, 0, 0));
        source.Add(Model, 2, ola, "Ola", Unit(0.9f, 0.1f, 0));
        var service = CreateService(source);
        Assert.False(service.IsLoaded);

        await service.EnsureLoadedAsync(default);
        await service.EnsureLoadedAsync(default);

        Assert.Equal(1, source.Loads);
        Assert.Equal(2, service.Index.EmbeddingCount);
        Assert.Equal(ola, service.Match(Unit(1, 0, 0), 0.9f).PersonId);
        Assert.Equal(1, source.Loads);
    }

    [Fact]
    public async Task Service_IgnoresEmbeddingsFromOtherModelVersions()
    {
        var source = new FakeEmbeddingSource();
        source.Add("modell-v1", 1, Guid.NewGuid(), "Ola", Unit(1, 0));

        var v2 = CreateService(source, "modell-v2");
        await v2.LoadAsync(default);
        Assert.Equal(0, v2.Index.EmbeddingCount);
        Assert.False(v2.Match(Unit(1, 0), 0.5f).IsMatch);

        var v1 = CreateService(source, "modell-v1");
        await v1.LoadAsync(default);
        Assert.Equal(1, v1.Index.EmbeddingCount);
    }

    [Fact]
    public async Task Service_ReloadReplacesIndexWithChangedData()
    {
        var source = new FakeEmbeddingSource();
        var ola = Guid.NewGuid();
        source.Add(Model, 1, ola, "Ola", Unit(1, 0));
        var service = CreateService(source);
        await service.LoadAsync(default);

        source.Add(Model, 2, Guid.NewGuid(), "Kari", Unit(0, 1));
        await service.LoadAsync(default);

        Assert.Equal(2, service.Index.PersonCount);
        Assert.Equal("Kari", service.Match(Unit(0, 1), 0.9f).PersonName);
    }
}
