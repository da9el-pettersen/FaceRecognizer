using FaceRecognizer.Database;
using FaceRecognizer.Database.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace FaceRecognizer.Database.Tests;

/// <summary>Tester for det varige lageret mot en ekte SQLite-fil og syntetiske vektorer.</summary>
public sealed class EmbeddingStoreTests : IDisposable
{
    private const string Model = "modell-v1";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "fr-db-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private EmbeddingStore CreateStore() => new(Path.Combine(_folder, "faces.db"));

    [Fact]
    public async Task Embeddings_overlever_ny_instans_og_er_normalisert_lagret_uten_bildeanalyse()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;
        var (status, result) = await store.AddImageAsync(person.PersonId, [1, 2, 3], Model, [1f, 0f, 0f], default);
        Assert.Equal(AddImageStatus.Added, status);
        Assert.NotNull(result);

        var snapshot = await CreateStore().LoadSnapshotAsync(Model, default);

        var item = Assert.Single(snapshot.Embeddings);
        Assert.Equal(person.PersonId, item.PersonId);
        Assert.Equal("Ola", item.PersonName);
        Assert.Equal([1f, 0f, 0f], item.Embedding);
    }

    [Fact]
    public async Task Samme_bilde_to_ganger_gir_Duplicate()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;
        await store.AddImageAsync(person.PersonId, [1], Model, [1f, 0f], default);

        var (status, _) = await store.AddImageAsync(person.PersonId, [1], Model, [1f, 0f], default);

        Assert.Equal(AddImageStatus.Duplicate, status);
        Assert.Single((await store.LoadSnapshotAsync(Model, default)).Embeddings);
    }

    [Fact]
    public async Task Ukjent_person_gir_PersonNotFound()
    {
        var (status, _) = await CreateStore().AddImageAsync(Guid.NewGuid(), [1], Model, [1f], default);

        Assert.Equal(AddImageStatus.PersonNotFound, status);
    }

    [Fact]
    public async Task Duplikat_navn_avvises_uavhengig_av_store_bokstaver()
    {
        var store = CreateStore();

        Assert.NotNull(await store.TryCreatePersonAsync("Ola", default));
        Assert.Null(await store.TryCreatePersonAsync("ola", default));
    }

    [Fact]
    public async Task Snapshot_skiller_modellversjoner_og_teller_andre()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;
        await store.AddImageAsync(person.PersonId, [1], "modell-v1", [1f, 0f], default);

        var v2 = await store.LoadSnapshotAsync("modell-v2", default);

        Assert.Empty(v2.Embeddings);
        Assert.Equal(1, v2.OtherModelEmbeddingCount);
        Assert.Equal(0, (await store.ListPersonsAsync("modell-v2", default)).Single().EmbeddingCount);
        Assert.Equal(1, (await store.ListPersonsAsync("modell-v1", default)).Single().EmbeddingCount);
    }

    [Fact]
    public async Task Bilder_uten_embedding_for_modellen_kan_listes_og_embeddes_pa_nytt()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;
        var (_, added) = await store.AddImageAsync(person.PersonId, [1], "modell-v1", [1f, 0f], default);

        var missing = Assert.Single(await store.ListImagesAsync("modell-v2", missingOnly: true, default));
        Assert.Empty(await store.ListImagesAsync("modell-v1", missingOnly: true, default));
        Assert.Single(await store.ListImagesAsync("modell-v1", missingOnly: false, default));
        Assert.Equal([1], await store.GetImageDataAsync(missing.ImageId, default));

        Assert.True(await store.SetEmbeddingAsync(added!.ImageId, "modell-v2", [0f, 1f], default));
        Assert.True(await store.SetEmbeddingAsync(added.ImageId, "modell-v2", [1f, 0f], default));
        var embedded = Assert.Single((await store.LoadSnapshotAsync("modell-v2", default)).Embeddings);
        Assert.Equal(new[] { 1f, 0f }, embedded.Embedding);
        Assert.False(await store.SetEmbeddingAsync(9999, "modell-v2", [1f], default));
    }

    [Fact]
    public async Task Sletting_av_siste_embedding_fjerner_bildet_og_person_sletting_fjerner_alt()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;
        var (_, first) = await store.AddImageAsync(person.PersonId, [1], Model, [1f, 0f], default);
        await store.AddImageAsync(person.PersonId, [2], Model, [0f, 1f], default);

        Assert.True(await store.DeleteEmbeddingAsync(first!.EmbeddingId, default));
        Assert.False(await store.DeleteEmbeddingAsync(first.EmbeddingId, default));
        Assert.Null(await store.GetImageDataAsync(first.ImageId, default));
        Assert.Single((await store.LoadSnapshotAsync(Model, default)).Embeddings);

        Assert.True(await store.DeletePersonAsync(person.PersonId, default));
        Assert.Empty((await store.LoadSnapshotAsync(Model, default)).Embeddings);
        Assert.Empty(await store.ListPersonsAsync(Model, default));
        Assert.False(await store.DeletePersonAsync(person.PersonId, default));
    }

    [Fact]
    public async Task Samtidige_skrivinger_og_lesinger_gir_konsistente_data()
    {
        var store = CreateStore();
        var person = (await store.TryCreatePersonAsync("Ola", default))!;

        await Task.WhenAll(Enumerable.Range(0, 20).Select(async i =>
        {
            await store.AddImageAsync(person.PersonId, [(byte)i], Model, [i + 1f, 0f], default);
            await store.LoadSnapshotAsync(Model, default);
        }));

        Assert.Equal(20, (await store.LoadSnapshotAsync(Model, default)).Embeddings.Count);
    }

    [Fact]
    public async Task Import_av_eldre_bilder_er_idempotent_og_gir_bilder_uten_embedding()
    {
        var legacy = Path.Combine(_folder, "faces");
        Directory.CreateDirectory(Path.Combine(legacy, "Ola"));
        Directory.CreateDirectory(Path.Combine(legacy, "ugyldig..navn!"));
        await File.WriteAllBytesAsync(Path.Combine(legacy, "Ola", "a.jpg"), [7, 7]);
        await File.WriteAllBytesAsync(Path.Combine(legacy, "Ola", "notat.txt"), [1]);
        await File.WriteAllBytesAsync(Path.Combine(legacy, "ugyldig..navn!", "a.jpg"), [8]);
        var store = CreateStore();

        Assert.Equal(1, await LegacyImport.RunAsync(store, legacy, NullLogger.Instance, default));
        Assert.Equal(0, await LegacyImport.RunAsync(store, legacy, NullLogger.Instance, default));

        Assert.Single(await store.ListImagesAsync(Model, missingOnly: true, default));
        Assert.Equal("Ola", (await store.ListPersonsAsync(Model, default)).Single().Name);
    }
}
