namespace FaceRecognizer.Recognition.Engines;

/// <summary>En person i minneindeksen med normaliserte embeddings.</summary>
/// <param name="PersonId">Unik ID.</param>
/// <param name="Name">Visningsnavn.</param>
/// <param name="EmbeddingIds">Database-ID for hver embedding (samme rekkefølge som <paramref name="Embeddings"/>).</param>
/// <param name="Embeddings">Normaliserte embeddings.</param>
public sealed record IndexedPerson(Guid PersonId, string Name, long[] EmbeddingIds, float[][] Embeddings);

/// <summary>Beste treff i indeksen.</summary>
/// <param name="PersonId">Person med høyest likhet.</param>
/// <param name="Name">Personens navn.</param>
/// <param name="Score">Cosinus-likhet (høyeste blant personens embeddings).</param>
public readonly record struct IndexMatch(Guid PersonId, string Name, float Score);

/// <summary>
/// Minnebasert indeks over normaliserte embeddings, én modellversjon om gangen.
/// Lesing er låsefri mot et uforanderlig øyeblikksbilde; skriving bytter ut øyeblikksbildet (copy-on-write)
/// og er serialisert. Søk gjør ingen allokeringer og ingen databaseoppslag.
/// </summary>
public sealed class EmbeddingIndex
{
    private readonly object _writeLock = new();
    private volatile Dictionary<Guid, IndexedPerson> _persons = [];

    /// <summary>Oppretter en tom indeks.</summary>
    /// <param name="modelId">Modellversjonen alle embeddings i indeksen tilhører.</param>
    public EmbeddingIndex(string modelId) => ModelId = modelId;

    /// <summary>Modellversjonen indeksen er bygget for.</summary>
    public string ModelId { get; }

    /// <summary>Antall personer.</summary>
    public int PersonCount => _persons.Count;

    /// <summary>Totalt antall embeddings.</summary>
    public int EmbeddingCount
    {
        get
        {
            var total = 0;
            foreach (var person in _persons.Values)
            {
                total += person.Embeddings.Length;
            }

            return total;
        }
    }

    /// <summary>Alle personer i gjeldende øyeblikksbilde.</summary>
    public IReadOnlyCollection<IndexedPerson> Persons => _persons.Values;

    /// <summary>Bytter ut hele indeksen atomisk.</summary>
    /// <param name="entries">Embeddings som skal indekseres. Må være normalisert og ha lik dimensjon.</param>
    /// <exception cref="ArgumentException">Ulik dimensjon eller ikke-normalisert vektor.</exception>
    public void ReplaceAll(IEnumerable<(Guid PersonId, string Name, long EmbeddingId, float[] Embedding)> entries)
    {
        var grouped = new Dictionary<Guid, (string Name, List<long> Ids, List<float[]> Vectors)>();
        var dimension = 0;
        foreach (var (personId, name, embeddingId, embedding) in entries)
        {
            Validate(embedding, ref dimension);
            if (!grouped.TryGetValue(personId, out var group))
            {
                group = (name, [], []);
                grouped[personId] = group;
            }

            group.Ids.Add(embeddingId);
            group.Vectors.Add(embedding);
        }

        var next = new Dictionary<Guid, IndexedPerson>(grouped.Count);
        foreach (var (personId, group) in grouped)
        {
            next[personId] = new IndexedPerson(personId, group.Name, [.. group.Ids], [.. group.Vectors]);
        }

        lock (_writeLock)
        {
            _persons = next;
        }
    }

    /// <summary>Legger til en embedding for en person (personen opprettes i indeksen ved behov).</summary>
    /// <param name="personId">Person-ID.</param>
    /// <param name="name">Personens navn.</param>
    /// <param name="embeddingId">Database-ID for embeddingen.</param>
    /// <param name="embedding">Normalisert embedding.</param>
    /// <exception cref="ArgumentException">Feil dimensjon eller ikke-normalisert vektor.</exception>
    public void Add(Guid personId, string name, long embeddingId, float[] embedding)
    {
        lock (_writeLock)
        {
            var dimension = CurrentDimension();
            Validate(embedding, ref dimension);
            var next = new Dictionary<Guid, IndexedPerson>(_persons);
            next[personId] = next.TryGetValue(personId, out var existing)
                ? existing with
                {
                    Name = name,
                    EmbeddingIds = [.. existing.EmbeddingIds, embeddingId],
                    Embeddings = [.. existing.Embeddings, embedding]
                }
                : new IndexedPerson(personId, name, [embeddingId], [embedding]);
            _persons = next;
        }
    }

    /// <summary>Fjerner én embedding. Personen fjernes fra indeksen når den siste embeddingen er borte.</summary>
    /// <param name="embeddingId">Database-ID for embeddingen.</param>
    /// <returns><c>true</c> hvis embeddingen fantes.</returns>
    public bool RemoveEmbedding(long embeddingId)
    {
        lock (_writeLock)
        {
            foreach (var person in _persons.Values)
            {
                var position = Array.IndexOf(person.EmbeddingIds, embeddingId);
                if (position < 0)
                {
                    continue;
                }

                var next = new Dictionary<Guid, IndexedPerson>(_persons);
                if (person.Embeddings.Length == 1)
                {
                    next.Remove(person.PersonId);
                }
                else
                {
                    next[person.PersonId] = person with
                    {
                        EmbeddingIds = [.. person.EmbeddingIds.Where((_, i) => i != position)],
                        Embeddings = [.. person.Embeddings.Where((_, i) => i != position)]
                    };
                }

                _persons = next;
                return true;
            }

            return false;
        }
    }

    /// <summary>Fjerner en person og alle embeddings.</summary>
    /// <param name="personId">Person-ID.</param>
    /// <returns><c>true</c> hvis personen fantes i indeksen.</returns>
    public bool RemovePerson(Guid personId)
    {
        lock (_writeLock)
        {
            if (!_persons.ContainsKey(personId))
            {
                return false;
            }

            var next = new Dictionary<Guid, IndexedPerson>(_persons);
            next.Remove(personId);
            _persons = next;
            return true;
        }
    }

    /// <summary>Finner personen med høyest cosinus-likhet.</summary>
    /// <param name="query">Normalisert embedding for ansiktet som søkes.</param>
    /// <param name="match">Beste treff, uavhengig av terskel.</param>
    /// <returns><c>false</c> hvis indeksen er tom eller ingen embeddings har samme dimensjon som <paramref name="query"/>.</returns>
    public bool TryFindBest(ReadOnlySpan<float> query, out IndexMatch match)
    {
        var snapshot = _persons;
        var found = false;
        var bestScore = float.MinValue;
        IndexedPerson? bestPerson = null;
        foreach (var person in snapshot.Values)
        {
            foreach (var reference in person.Embeddings)
            {
                if (reference.Length != query.Length)
                {
                    continue;
                }

                var score = EmbeddingMath.Dot(query, reference);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPerson = person;
                    found = true;
                }
            }
        }

        match = found ? new IndexMatch(bestPerson!.PersonId, bestPerson.Name, bestScore) : default;
        return found;
    }

    private int CurrentDimension()
    {
        foreach (var person in _persons.Values)
        {
            if (person.Embeddings.Length > 0)
            {
                return person.Embeddings[0].Length;
            }
        }

        return 0;
    }

    private static void Validate(float[] embedding, ref int dimension)
    {
        if (embedding.Length == 0 || (dimension != 0 && embedding.Length != dimension))
        {
            throw new ArgumentException("Embeddingen har feil dimensjon for denne indeksen.", nameof(embedding));
        }

        dimension = embedding.Length;
        var norm = EmbeddingMath.Dot(embedding, embedding);
        if (!float.IsFinite(norm) || MathF.Abs(norm - 1f) > 0.01f)
        {
            throw new ArgumentException("Embeddingen må være normalisert.", nameof(embedding));
        }
    }
}
