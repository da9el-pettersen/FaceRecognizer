using System.Text.RegularExpressions;

namespace FaceRecognizer.Contracts;

/// <summary>Felles regler for personnavn, slik at alle tjenester validerer likt.</summary>
public static partial class PersonNames
{
    [GeneratedRegex(@"^[\p{L}\p{N}_ -]{1,64}$")]
    private static partial Regex Pattern();

    /// <summary>Om navnet er gyldig (bokstaver, tall, mellomrom, understrek og bindestrek, 1-64 tegn).</summary>
    /// <param name="name">Navnet som sjekkes.</param>
    public static bool IsValid(string? name) => name is not null && Pattern().IsMatch(name);
}

/// <summary>Regler for koblingen mellom en person og en Home Assistant person-entitet.</summary>
public static partial class HaPersonEntities
{
    [GeneratedRegex(@"^person\.[a-z0-9_]{1,64}$")]
    private static partial Regex Pattern();

    /// <summary>Om verdien er en gyldig entitets-ID, f.eks. <c>person.daniel</c>.</summary>
    /// <param name="entityId">Entitets-ID.</param>
    public static bool IsValid(string? entityId) => entityId is not null && Pattern().IsMatch(entityId);
}
