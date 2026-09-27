using System.Numerics;

namespace MhoPackageModifier;

/// <summary>
/// Per-zone facts the GUI fills in for the user: the tile layout (ZoneData/&lt;Zone&gt;/layout.txt), the region library the
/// tiles take their meshes from, and the offset from tile coordinates to in-game positions (MHServerEmu centres a region
/// on its area bounds: Hightown +5208, -15512). Tiled zones store every placement in world coordinates, one tile file
/// per place, so placements can be edited in Blender; zones whose cells are placed at run time (Industry City) reuse one
/// cell file in several places, so the placement round trip isn't offered there (the placed-mesh export still works).
/// </summary>
static class ZonePresets
{
    public sealed record Preset(string Name, string Display, string MainLevel, string Library, Vector3 Offset, bool Tiled, string Notes)
    {
        public string Layout => Path.Combine(AppContext.BaseDirectory, "ZoneData", Name, "layout.txt");
        public override string ToString() => Display;
    }

    public static readonly Preset[] All =
    [
        new("Hightown", "Hightown (Upper Madripoor)", "Madripoor_HighTown_B.upk", "SCS__DailyRHighTownInvasionRegionL30_SF.upk", new(5208, -15512, 0), true,
            "58 tiles, meshes from SCS__DailyRHighTownInvasionRegionL30_SF, offset +5208, -15512 (region centring)."),
        new("OdinsPalace", "Odin's Palace (Asgard)", "Asgard_Hub_B.upk", "SCS__DailyGAsgardINSTRegionL40_SF.upk", Vector3.Zero, true,
            "12 Asgardia tiles + 2 bridge cells, meshes from SCS__DailyGAsgardINSTRegionL40_SF, no offset."),
        new("IndustryCity", "Industry City (ICP)", "Brooklyn_Docks_A.upk", "SCS__CH0201ShippingYardRegion_SF.upk", Vector3.Zero, false,
            "Cells are placed at run time (one cell file is used in several places, and the middle block changes between runs), so placements can't be edited per place. The placed-mesh export works."),
    ];
}
