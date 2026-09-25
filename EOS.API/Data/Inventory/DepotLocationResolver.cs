namespace EOS.API.Data.Inventory;

/// <summary>Where the resolved location came from, so the caller can say why this one was chosen.</summary>
public enum LocationResolutionSource
{
    /// <summary>Configured default location of the material (STORAGE_MODE = FIXED).</summary>
    PrimaryLocation,

    /// <summary>Level 1: a location this material already occupies in this depot.</summary>
    Occupied,

    /// <summary>Level 2: the location this material was last put into.</summary>
    LastPlaced,

    /// <summary>Level 3: a location inside the area that currently holds no stock.</summary>
    EmptyInArea,

    /// <summary>Nothing resolved: fall back to the sentinel. This is a normal outcome, not an error.</summary>
    Sentinel,
}

/// <summary>Result of one resolution: the location to use plus the reason it was chosen.</summary>
public sealed record LocationResolution(string LocationNo, LocationResolutionSource Source)
{
    /// <summary>True when nothing resolved and the location is the "not specified" sentinel.</summary>
    public bool IsSentinel => Source == LocationResolutionSource.Sentinel;
}

/// <summary>
/// Everything the resolution needs, already read from the database by the caller. Splitting "what to look
/// up" from "what was found" keeps the resolution itself a pure function: no connection, no query, and the
/// whole decision table can be enumerated in unit tests.
/// </summary>
/// <param name="StorageMode">Deployment-level stock policy `STORAGE_MODE`: FIXED / RANDOM / MIXED (case-insensitive).</param>
/// <param name="Sentinel">Location sentinel of the calling context; defaults to <see cref="DepotLocationResolver.DefaultSentinel"/>.</param>
/// <param name="PrimaryLocations">DEPOT_PRODUCT_LOCATION rows of this material/depot with IS_PRIMARY=1, in priority order.</param>
/// <param name="OccupiedLocation">Level 1 candidate: a location this material already occupies in this depot.</param>
/// <param name="LastPlacedLocation">Level 2 candidate: the location of the most recent ledger row for this material/depot.</param>
/// <param name="EmptyLocationsInArea">Level 3 candidates: locations of the target area holding no stock, ascending by SEQ_NO.</param>
public sealed record LocationResolutionInput(
    string? StorageMode,
    string? Sentinel,
    IReadOnlyList<string>? PrimaryLocations,
    string? OccupiedLocation,
    string? LastPlacedLocation,
    IReadOnlyList<string>? EmptyLocationsInArea)
{
    public LocationResolution Resolve() => DepotLocationResolver.Resolve(this);
}

/// <summary>
/// Picks the location an inbound movement should be written to (three-level fallback, no capacity check).
/// </summary>
/// <remarks>
/// The decision table is deliberately tiny and open about its limits:
/// <list type="bullet">
/// <item>FIXED: only the material's default location counts; when it has none the sentinel is returned and
/// nothing throws — a missing default is a configuration gap, not a failure of the calling document.</item>
/// <item>RANDOM / MIXED: level 1 (already occupied) → level 2 (last placed) → level 3 (first empty location
/// of the area, candidates already ordered by SEQ_NO by the caller) → sentinel.</item>
/// <item>Any other STORAGE_MODE resolves to the sentinel: the location is part of the stock key, so guessing
/// is more expensive than not choosing.</item>
/// <item>Blank values and the sentinel itself are never treated as a real placement — the ledger stores "-"
/// for "no location" and that must not be handed back as a location.</item>
/// </list>
/// The caller keeps two duties this type does not take over: narrowing the area / restricting candidates to
/// existing, active locations, and running the resolved value through the location-existence pre-check.
/// </remarks>
public static class DepotLocationResolver
{
    public const string FixedMode = "FIXED";
    public const string RandomMode = "RANDOM";
    public const string MixedMode = "MIXED";

    /// <summary>Location of "not specified / to be put away"; a real column value, not NULL.</summary>
    public const string DefaultSentinel = "-";

    public static LocationResolution Resolve(LocationResolutionInput input)
    {
        var sentinel = Normalize(input.Sentinel) ?? DefaultSentinel;
        var mode = (input.StorageMode ?? string.Empty).Trim().ToUpperInvariant();

        if (mode == FixedMode)
        {
            return Usable(input.PrimaryLocations, sentinel) is { } primary
                ? new LocationResolution(primary, LocationResolutionSource.PrimaryLocation)
                : new LocationResolution(sentinel, LocationResolutionSource.Sentinel);
        }

        if (mode == RandomMode || mode == MixedMode)
        {
            if (Usable(input.OccupiedLocation, sentinel) is { } occupied)
                return new LocationResolution(occupied, LocationResolutionSource.Occupied);

            if (Usable(input.LastPlacedLocation, sentinel) is { } lastPlaced)
                return new LocationResolution(lastPlaced, LocationResolutionSource.LastPlaced);

            if (Usable(input.EmptyLocationsInArea, sentinel) is { } empty)
                return new LocationResolution(empty, LocationResolutionSource.EmptyInArea);

            return new LocationResolution(sentinel, LocationResolutionSource.Sentinel);
        }

        return new LocationResolution(sentinel, LocationResolutionSource.Sentinel);
    }

    /// <summary>First value that is a real location; null when the sequence is empty or all entries are blank/sentinel.</summary>
    private static string? Usable(IReadOnlyList<string>? candidates, string sentinel)
    {
        if (candidates is null) return null;
        foreach (var candidate in candidates)
            if (Usable(candidate, sentinel) is { } hit) return hit;
        return null;
    }

    private static string? Usable(string? candidate, string sentinel)
        => Normalize(candidate) is { } value && !string.Equals(value, sentinel, StringComparison.Ordinal) ? value : null;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
