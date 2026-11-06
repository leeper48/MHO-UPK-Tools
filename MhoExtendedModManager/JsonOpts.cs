using System.Text.Json;

namespace MhoExtendedModManager;

/// <summary>
/// (perf patch) Shared JSON settings. A new JsonSerializerOptions on every save builds its type metadata again (generated
/// code included), which then has to be finalized: in a user's profile (2026-11-04) the finalizer thread spent ~10% of the
/// app's CPU cleaning those up. One instance caches it for the whole session; the output is the same.
/// </summary>
static class JsonOpts
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    public static readonly JsonSerializerOptions CaseInsensitive = new() { PropertyNameCaseInsensitive = true };
}
