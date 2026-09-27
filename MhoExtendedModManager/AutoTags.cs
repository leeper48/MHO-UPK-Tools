namespace MhoExtendedModManager;

/// <summary>
/// Automatic tags, worked out from what a mod contains (never stored): the characters and their teams, and what kind of
/// change it is. Read from the names the game uses (checked on Kurt's library, 2026-09-27):
///   UC__MarvelPlayer_&lt;Hero&gt;[_&lt;Costume&gt;]_SF   costume (hero model)          → Costume
///   UC__MarvelTeamUp_&lt;Hero&gt;_SF                 team-up                        → Team-Up
///   UC__MarvelNPC_&lt;Name&gt;_SF                    NPC                            → NPC
///   UC__MarvelAgent_…Pet_SF                   pet                            → Pet  (other agents: the character only)
///   UC__PowerTeamUp_&lt;Hero&gt;_…                  team-up powers                 → Team-up, Power effects
///   UC__Power&lt;Hero&gt;_…, UC__MarvelProjectile_…, UC__MarvelEntity_Hotspot_…, UC__MarvelConditionEffect_…
///                                             power visuals                  → Power effects
///   other packages (tiles, SCS__ libraries)   zones                          → Zone
///   textures costume[_]&lt;hero&gt;_… → Costume; power_&lt;hero&gt;_… → Power icons; teamup_… → Team-up;
///   store_&lt;hero&gt;_…, herohor_&lt;hero&gt;_… → the hero only.   Sound packs → Sounds.
/// </summary>
static class AutoTags
{
    const string XMen = "X-Men", Avengers = "Avengers", Guardians = "Guardians of the Galaxy", FF = "Fantastic Four", Inhumans = "Inhumans",
                 Defenders = "Defenders", Knights = "Marvel Knights", Shield = "S.H.I.E.L.D.", Asgard = "Asgardians", XForce = "X-Force",
                 Spider = "Spider-Verse", Cosmic = "Cosmic", Villains = "Villains";

    /// <summary>The game's character ids (lower case, as in package and texture names) → display name and teams.</summary>
    static readonly Dictionary<string, (string Name, string[] Teams)> Heroes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Playable heroes (UC__MarvelPlayer_*)
        ["angela"] = ("Angela", [Guardians, Asgard]), ["antman"] = ("Ant-Man", [Avengers]), ["beast"] = ("Beast", [XMen, Avengers]),
        ["blackbolt"] = ("Black Bolt", [Inhumans]), ["blackcat"] = ("Black Cat", [Spider]), ["blackpanther"] = ("Black Panther", [Avengers]),
        ["blackwidow"] = ("Black Widow", [Avengers, Shield]), ["blade"] = ("Blade", [Knights]), ["cable"] = ("Cable", [XMen, XForce]),
        ["captainamerica"] = ("Captain America", [Avengers]), ["carnage"] = ("Carnage", [Villains, Spider]), ["colossus"] = ("Colossus", [XMen]),
        ["cyclops"] = ("Cyclops", [XMen]), ["daredevil"] = ("Daredevil", [Defenders, Knights]), ["deadpool"] = ("Deadpool", [XForce]),
        ["doctorstrange"] = ("Doctor Strange", [Defenders, Avengers]), ["drdoom"] = ("Doctor Doom", [Villains]), ["elektra"] = ("Elektra", [Knights]),
        ["emmafrost"] = ("Emma Frost", [XMen]), ["gambit"] = ("Gambit", [XMen]), ["ghostrider"] = ("Ghost Rider", [Knights]),
        ["greengoblin"] = ("Green Goblin", [Villains, Spider]), ["hawkeye"] = ("Hawkeye", [Avengers]), ["hulk"] = ("Hulk", [Avengers, Defenders]),
        ["humantorch"] = ("Human Torch", [FF]), ["iceman"] = ("Iceman", [XMen]), ["invisiblewoman"] = ("Invisible Woman", [FF]),
        ["ironfist"] = ("Iron Fist", [Defenders]), ["ironman"] = ("Iron Man", [Avengers]), ["jeangrey"] = ("Jean Grey", [XMen]),
        ["juggernaut"] = ("Juggernaut", [Villains]), ["katebishop"] = ("Kate Bishop", [Avengers]), ["kittypryde"] = ("Kitty Pryde", [XMen]),
        ["ladyloki"] = ("Lady Loki", [Asgard, Villains]), ["loki"] = ("Loki", [Asgard, Villains]), ["lukecage"] = ("Luke Cage", [Defenders]),
        ["magik"] = ("Magik", [XMen]), ["magneto"] = ("Magneto", [Villains]), ["moonknight"] = ("Moon Knight", [Knights]),
        ["mrfantastic"] = ("Mr. Fantastic", [FF]), ["msmarvel"] = ("Captain Marvel", [Avengers]), ["nickfury"] = ("Nick Fury", [Shield]),
        ["nightcrawler"] = ("Nightcrawler", [XMen]), ["nova"] = ("Nova", [Cosmic]), ["psylocke"] = ("Psylocke", [XMen]),
        ["punisher"] = ("Punisher", [Knights]), ["rocketraccoon"] = ("Rocket Raccoon", [Guardians]), ["rogue"] = ("Rogue", [XMen]),
        ["scarletwitch"] = ("Scarlet Witch", [Avengers]), ["shehulk"] = ("She-Hulk", [Avengers]), ["silversurfer"] = ("Silver Surfer", [Cosmic]),
        ["spiderman"] = ("Spider-Man", [Spider, Avengers]), ["squirrelgirl"] = ("Squirrel Girl", [Avengers]), ["starlord"] = ("Star-Lord", [Guardians]),
        ["storm"] = ("Storm", [XMen]), ["taskmaster"] = ("Taskmaster", [Villains]), ["thing"] = ("Thing", [FF]), ["thor"] = ("Thor", [Avengers, Asgard]),
        ["ultron"] = ("Ultron", [Villains]), ["venom"] = ("Venom", [Spider]), ["vision"] = ("Vision", [Avengers]), ["warmachine"] = ("War Machine", [Avengers]),
        ["wintersoldier"] = ("Winter Soldier", [Avengers]), ["wolverine"] = ("Wolverine", [XMen, Avengers, XForce]), ["x23"] = ("X-23", [XMen, XForce]),
        // Team-ups (UC__MarvelTeamUp_*)
        ["agent13"] = ("Agent 13", [Shield]), ["agentcoulson"] = ("Agent Coulson", [Shield]), ["agentvenom"] = ("Agent Venom", [Spider]),
        ["angel"] = ("Angel", [XMen]), ["arachne"] = ("Arachne", [Spider]), ["archangel"] = ("Archangel", [XMen, XForce]),
        ["betaraybill"] = ("Beta Ray Bill", [Asgard]), ["clea"] = ("Clea", [Defenders]), ["domino"] = ("Domino", [XForce]),
        ["drax"] = ("Drax", [Guardians]), ["falcon"] = ("Falcon", [Avengers]), ["firestar"] = ("Firestar", [Avengers]),
        ["frankencastle"] = ("Frankencastle", [Knights]), ["gamora"] = ("Gamora", [Guardians]), ["groot"] = ("Groot", [Guardians]),
        ["gwenstacy"] = ("Spider-Gwen", [Spider]), ["havok"] = ("Havok", [XMen]), ["howardtheduck"] = ("Howard the Duck", []),
        ["hydraagent"] = ("Hydra Agent", [Villains]), ["jessicajones"] = ("Jessica Jones", [Defenders]), ["jubilee"] = ("Jubilee", [XMen]),
        ["kamalakhan"] = ("Kamala Khan", [Avengers]), ["medusa"] = ("Medusa", [Inhumans]), ["milesmorales"] = ("Miles Morales", [Spider]),
        ["mordo"] = ("Mordo", [Villains]), ["oldmanlogan"] = ("Old Man Logan", [XMen]), ["quake"] = ("Quake", [Shield, Avengers]),
        ["quicksilver"] = ("Quicksilver", [Avengers]), ["rescue"] = ("Rescue", [Avengers]), ["robbiereyes"] = ("Robbie Reyes", [Knights]),
        ["samwilson"] = ("Sam Wilson", [Avengers]), ["shieldagent"] = ("S.H.I.E.L.D. Agent", [Shield]), ["spiderwoman"] = ("Spider-Woman", [Avengers]),
        ["sunspot"] = ("Sunspot", [XMen]), ["wasp"] = ("Wasp", [Avengers]),
        // NPCs seen in mods (UC__MarvelNPC_*)
        ["professorx"] = ("Professor X", [XMen]), ["forge"] = ("Forge", [XMen]), ["morph"] = ("Morph", [XMen]),
    };

    /// <summary>Other spellings the game uses for the same character.</summary>
    static readonly Dictionary<string, string> Alias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["capnamerica"] = "captainamerica", ["drstrange"] = "doctorstrange", ["captainmarvel"] = "msmarvel", ["holidaydoctordoom"] = "drdoom",
        ["piratedeadpool"] = "deadpool", ["rocketraccoonmech"] = "rocketraccoon",
    };

    /// <summary>The character a name part stands for: exact, an alias, or the longest id it starts with
    /// (DraxVol2 → Drax, WolverineBrood → Wolverine, IronmanMark2 → Iron Man).</summary>
    static string? Hero(string token)
    {
        string t = token.ToLowerInvariant();
        if (Alias.TryGetValue(t, out var a)) t = a;
        if (Heroes.ContainsKey(t)) return t;
        return Heroes.Keys.Concat(Alias.Keys).Where(k => k.Length >= 4 && t.StartsWith(k, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(k => k.Length).Select(k => Alias.TryGetValue(k, out var b) ? b : k).FirstOrDefault();
    }

    /// <summary>The first character id in a name part, longest at that place (UC__MarvelProjectile_ScarletWitchShadowBolt…;
    /// Rogue_StolenPower_ColossusInvulnerability is Rogue's).</summary>
    static string? HeroInside(string text)
    {
        string t = text.ToLowerInvariant();
        return Heroes.Keys.Concat(Alias.Keys).Where(k => k.Length >= 4 && t.Contains(k.ToLowerInvariant()))
                     .OrderBy(k => t.IndexOf(k.ToLowerInvariant(), StringComparison.Ordinal)).ThenByDescending(k => k.Length)
                     .Select(k => Alias.TryGetValue(k, out var b) ? b : k).FirstOrDefault();
    }

    /// <summary>A character's display name for a name part of a package ("Spiderman" → "Spider-Man"), or null.</summary>
    public static string? DisplayName(string token) => Hero(token) is string id ? Heroes[id].Name : null;

    /// <summary>Every spelling the game uses for a package name part's character (DoctorStrange → doctorstrange, drstrange).</summary>
    public static List<string> Spellings(string token)
    {
        var list = new List<string> { token.ToLowerInvariant() };
        if (Hero(token) is string id)
        {
            list.Add(id);
            list.AddRange(Alias.Where(a => a.Value == id).Select(a => a.Key.ToLowerInvariant()));
        }
        return list.Distinct().ToList();
    }

    public enum TagClass { Character, Team, Content, Other }
    static readonly string[] Kinds = ["Costume", "Team-Up", "NPC", "Power Effects", "Power Icons", "Pet", "Zone", "Sounds"];
    static readonly HashSet<string> CharacterNames = new(Heroes.Values.Select(h => h.Name), StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> TeamNames = new(Heroes.Values.SelectMany(h => h.Teams), StringComparer.OrdinalIgnoreCase);

    /// <summary>What a tag names, whoever set it: a character, a team, a kind of content, or something else (tag colours).</summary>
    public static TagClass Classify(string tag) =>
        CharacterNames.Contains(tag) ? TagClass.Character : TeamNames.Contains(tag) ? TagClass.Team
        : Kinds.Contains(tag, StringComparer.OrdinalIgnoreCase) ? TagClass.Content : TagClass.Other;

    public static List<string> For(ModManifest m)
    {
        // Characters are counted: a mod mostly about one hero can touch a few others in passing (Rogue's stolen-power
        // icons use power_beast, teamup_havok …: 1 each against 113 of her own), and those aren't tagged.
        var heroes = new List<string>();
        var hits = new Dictionary<string, int>();
        var kinds = new List<string>();
        int weight = 4;   // a package counts 4 icons: the NPC pack's one package per character outweighs Beast's 3 icons
        void AddHero(string? id) { if (id == null) return; if (!heroes.Contains(id)) heroes.Add(id); hits[id] = hits.GetValueOrDefault(id) + weight; }
        void AddKind(string k) { if (!kinds.Contains(k)) kinds.Add(k); }
        static string Part(string s, int i) { var p = s.Split('_', StringSplitOptions.RemoveEmptyEntries); return i < p.Length ? p[i] : ""; }

        foreach (string file in m.UpkReplacements)
        {
            string n = Path.GetFileNameWithoutExtension(file);
            string l = n.ToLowerInvariant();
            if (l.StartsWith("uc__marvelplayer_")) { AddHero(Hero(Part(n[17..], 0))); AddKind("Costume"); }
            else if (l.StartsWith("uc__marvelteamup_")) { AddHero(Hero(Part(n[17..], 0))); AddKind("Team-Up"); }
            else if (l.StartsWith("uc__marvelnpc_")) { AddHero(Hero(Part(n[14..], 0))); AddKind("NPC"); }
            else if (l.StartsWith("uc__powerteamup_")) { AddHero(Hero(Part(n[16..], 0))); AddKind("Team-Up"); AddKind("Power Effects"); }
            else if (l.StartsWith("uc__power")) { AddHero(Hero(Part(n[9..], 0))); AddKind("Power Effects"); }
            else if (l.StartsWith("uc__marvelagent_"))
            {
                // Agents: pets (…Pet), bosses and enemies. A pet adds only "Pet" (Unique333GambitPet isn't a Gambit mod).
                if (l.EndsWith("pet_sf") || l.Contains("pet_")) AddKind("Pet");
                else AddHero(HeroInside(n[16..]));
            }
            else if (l.StartsWith("uc__marvelprojectile_") || l.StartsWith("uc__marvelentity_hotspot_") || l.StartsWith("uc__marvelconditioneffect_"))
            { AddHero(HeroInside(n[(n.IndexOf('_', 4) + 1)..])); AddKind("Power Effects"); }
            else if (!l.StartsWith("uc__") && !l.StartsWith("ico__")) AddKind("Zone");
        }

        weight = 1;
        foreach (string tex in m.Replacements.Concat(m.StoreReplacements).Select(r => r.TextureName).Concat(m.Extra.Select(r => r.TextureName)))
        {
            string l = (tex ?? "").ToLowerInvariant();
            if (l.StartsWith("costume"))
            {
                string rest = l[7..].TrimStart('_');
                if (rest.StartsWith("teamup")) { AddHero(Hero(Part(rest.Length > 6 ? rest[6..] : "", 0))); AddKind("Team-Up"); }
                else AddHero(Hero(Part(rest, 0)));
                AddKind("Costume");
            }
            else if (l.StartsWith("power_")) { AddHero(Hero(Part(l, 1))); AddKind("Power Icons"); }
            else if (l.StartsWith("teamup_")) { AddHero(Hero(Part(l, 1))); AddKind("Team-Up"); }
            else if (l.StartsWith("store_") || l.StartsWith("herohor_"))
            {
                string p1 = Part(l, 1);
                if (p1 == "teamup") AddHero(Hero(Part(l, 2))); else AddHero(Hero(p1));
            }
        }
        if (m.AudioPacks.Count > 0) AddKind("Sounds");

        int top = hits.Count > 0 ? hits.Values.Max() : 0;
        heroes = heroes.Where(h => hits[h] * 4 >= top).ToList();
        var tags = new List<string>();
        foreach (string h in heroes.Take(6)) tags.Add(Heroes[h].Name);
        foreach (string team in heroes.SelectMany(h => Heroes[h].Teams)) if (!tags.Contains(team)) tags.Add(team);
        tags.AddRange(kinds);
        return tags;
    }
}
