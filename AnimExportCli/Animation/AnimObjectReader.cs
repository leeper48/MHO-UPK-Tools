using AnimExportCli.Packages;
using AnimExportCli.Packages.Properties;

namespace AnimExportCli.Animation;

/// <summary>
/// Reads animation objects the same way <see cref="Meshes.SkeletalMeshReader"/>
/// reads a mesh: an AnimSet or AnimSequence is, mechanically, just another
/// tagged-property object — <c>TrackBoneNames</c>, <c>Sequences</c>,
/// <c>SequenceName</c>, <c>NumFrames</c> and so on are ordinary properties, not
/// anything requiring a general-purpose reflection layer. Only the compressed
/// track data after the property block needs dedicated binary decoding.
/// </summary>
/// <remarks>
/// The array-property wire format used here — a leading element count
/// followed by that many fixed-width elements — and the anim-track
/// compression schemes below (<see cref="AnimationCompressionFormat"/>,
/// <see cref="AnimationKeyFormat"/>) are standard UE3-generation conventions
/// documented across the wider modding community, not something specific to
/// any one game's cook. That makes them safe to implement directly against
/// that general knowledge, unlike the mesh binary layout, which had to be
/// ported from packages verified byte-for-byte.
/// </remarks>
public static class AnimObjectReader
{
    public const string AnimSetClassName = "animset";
    public const string AnimSequenceClassName = "animsequence";

    public sealed record AnimSetInfo(int ExportIndex, IReadOnlyList<string> TrackBoneNames, IReadOnlyList<ObjectReference> Sequences);

    public static IEnumerable<AnimSetInfo> FindAnimSets(Package package)
    {
        foreach (int index in package.FindExportsOfClass(AnimSetClassName))
        {
            PropertyBag? properties = package.TryReadProperties(index);
            if (properties is null) continue;

            PropertyTag? trackBoneNamesTag = properties.Find("TrackBoneNames");
            PropertyTag? sequencesTag = properties.Find("Sequences");
            if (trackBoneNamesTag is null || sequencesTag is null) continue;

            List<string> trackBoneNames = DecodeNameArray(trackBoneNamesTag, package.Names);
            if (trackBoneNames.Count == 0) continue;

            yield return new AnimSetInfo(index, trackBoneNames, DecodeObjectArray(sequencesTag));
        }
    }

    /// <summary>The friendly clip name for an AnimSequence export: its SequenceName property, falling back to the export's own object name.</summary>
    public static string GetSequenceDisplayName(Package package, int exportIndex)
    {
        PropertyBag? properties = package.TryReadProperties(exportIndex);
        string sequenceName = properties?.GetName("SequenceName") ?? string.Empty;
        return string.IsNullOrWhiteSpace(sequenceName) ? package.GetExportName(exportIndex) : sequenceName;
    }

    /// <summary>
    /// Prints raw bytes and decoded values for one sequence's tracks, for
    /// eyeballing against expectations instead of guessing at what a decode
    /// bug might be. Not used by the normal export path.
    /// </summary>
    public static void DumpSequenceDiagnostics(Package package, int exportIndex, IReadOnlyList<string> trackBoneNames, TextWriter output, string? boneFilter = null)
    {
        if (!string.Equals(package.GetExportClassName(exportIndex), AnimSequenceClassName, StringComparison.OrdinalIgnoreCase))
        {
            output.WriteLine($"Export {exportIndex} is not an AnimSequence.");
            return;
        }

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null)
        {
            output.WriteLine($"Export {exportIndex}: properties did not parse.");
            return;
        }

        string sequenceName = properties.GetName("SequenceName");
        float sequenceLength = properties.GetFloat("SequenceLength");
        int numFrames = properties.GetInt("NumFrames");
        string transFormatName = properties.GetName("TranslationCompressionFormat");
        string rotFormatName = properties.GetName("RotationCompressionFormat");
        string keyFormatName = properties.GetName("KeyEncodingFormat");

        output.WriteLine($"Sequence '{sequenceName}' (export {exportIndex}): NumFrames={numFrames} SequenceLength={sequenceLength}");
        output.WriteLine($"  TranslationCompressionFormat = '{transFormatName}' -> {ParseCompressionFormat(transFormatName, AnimationCompressionFormat.None)}");
        output.WriteLine($"  RotationCompressionFormat    = '{rotFormatName}' -> {ParseCompressionFormat(rotFormatName, AnimationCompressionFormat.Float96NoW)}");
        output.WriteLine($"  KeyEncodingFormat            = '{keyFormatName}' -> {ParseKeyFormat(keyFormatName)}");

        var translationFormat = ParseCompressionFormat(transFormatName, AnimationCompressionFormat.None);
        var rotationFormat = ParseCompressionFormat(rotFormatName, AnimationCompressionFormat.Float96NoW);
        var keyFormat = ParseKeyFormat(keyFormatName);

        PropertyTag? offsetsTag = properties.Find("CompressedTrackOffsets");
        if (offsetsTag is null)
        {
            output.WriteLine("  No CompressedTrackOffsets property found.");
            return;
        }

        output.WriteLine($"  CompressedTrackOffsets: {offsetsTag.Value.Length} raw property bytes");
        output.WriteLine($"    hex (first 64): {Convert.ToHexString(offsetsTag.Value.Span[..Math.Min(64, offsetsTag.Value.Length)])}");

        int[] trackOffsets = DecodeIntArray(offsetsTag);
        output.WriteLine($"    decoded as {trackOffsets.Length} ints: [{string.Join(", ", trackOffsets.Take(24))}{(trackOffsets.Length > 24 ? ", ..." : "")}]");

        ReadOnlySpan<byte> compressed = LocateCompressedStream(package.GetExportData(exportIndex), properties.PayloadOffset);
        output.WriteLine($"  Compressed byte stream: {compressed.Length} bytes");

        int trackCount = Math.Min(trackBoneNames.Count, trackOffsets.Length / 4);
        output.WriteLine($"  {trackCount} tracks (trackBoneNames.Count={trackBoneNames.Count}, CompressedTrackOffsets.Length/4={trackOffsets.Length / 4})");

        for (int i = 0; i < trackCount; i++)
        {
            int transOffset = trackOffsets[(i * 4) + 0];
            int transNumKeys = trackOffsets[(i * 4) + 1];
            int rotOffset = trackOffsets[(i * 4) + 2];
            int rotNumKeys = trackOffsets[(i * 4) + 3];

            output.WriteLine(
                $"  [{i}] bone='{trackBoneNames[i]}' transOffset={transOffset} transNumKeys={transNumKeys} " +
                $"rotOffset={rotOffset} rotNumKeys={rotNumKeys}");

            if (boneFilter is not null && trackBoneNames[i].Contains(boneFilter, StringComparison.OrdinalIgnoreCase))
            {
                List<BonePositionKey> posKeys = DecodePositionTrack(compressed, transOffset, transNumKeys, translationFormat, keyFormat, numFrames);
                output.WriteLine($"      -- decoded position keys ({posKeys.Count}) --");
                foreach (BonePositionKey k in posKeys)
                    output.WriteLine($"      frame={k.TimeFrame,-8:F2} pos=({k.Position.X:F3}, {k.Position.Y:F3}, {k.Position.Z:F3})");

                List<BoneRotationKey> rotKeys = DecodeRotationTrack(compressed, rotOffset, rotNumKeys, rotationFormat, keyFormat, numFrames);
                output.WriteLine($"      -- decoded rotation keys ({rotKeys.Count}), with dot-product against the previous key --");
                System.Numerics.Quaternion? previous = null;
                foreach (BoneRotationKey k in rotKeys)
                {
                    string dot = previous is null ? "n/a" : System.Numerics.Quaternion.Dot(previous.Value, k.Rotation).ToString("F4");
                    output.WriteLine($"      frame={k.TimeFrame,-8:F2} rot=({k.Rotation.X:F4}, {k.Rotation.Y:F4}, {k.Rotation.Z:F4}, {k.Rotation.W:F4}) dotVsPrev={dot}");
                    previous = k.Rotation;
                }

                continue;
            }

            if (rotOffset < 0 || rotOffset >= compressed.Length) continue;

            int dumpLen = Math.Min(12, compressed.Length - rotOffset);
            output.WriteLine($"      rotation bytes at {rotOffset}: {Convert.ToHexString(compressed.Slice(rotOffset, dumpLen))}");

            if (dumpLen < 12) continue;

            float x = BitConverter.ToSingle(compressed.Slice(rotOffset, 4));
            float y = BitConverter.ToSingle(compressed.Slice(rotOffset + 4, 4));
            float z = BitConverter.ToSingle(compressed.Slice(rotOffset + 8, 4));
            float lengthSquared = (x * x) + (y * y) + (z * z);
            float w = lengthSquared < 1f ? MathF.Sqrt(1f - lengthSquared) : 0f;

            output.WriteLine(
                $"      first key read as 3 floats: x={x:F4} y={y:F4} z={z:F4} (|xyz|^2={lengthSquared:F4}) -> derived w={w:F4}");
        }
    }

    /// <summary>
    /// Decodes one AnimSequence export into a <see cref="BoneAnimation"/>, given
    /// the bone names its owning AnimSet's tracks line up with by index. Returns
    /// null when nothing usable decoded (an unsupported compression scheme, or a
    /// genuinely static sequence with no stored motion).
    /// </summary>
    public static BoneAnimation? TryRead(Package package, int exportIndex, IReadOnlyList<string> trackBoneNames)
    {
        if (!string.Equals(package.GetExportClassName(exportIndex), AnimSequenceClassName, StringComparison.OrdinalIgnoreCase))
            return null;

        PropertyBag? properties = package.TryReadProperties(exportIndex);
        if (properties is null) return null;

        string sequenceName = properties.GetName("SequenceName");
        if (string.IsNullOrWhiteSpace(sequenceName)) sequenceName = package.GetExportName(exportIndex);

        float sequenceLength = properties.GetFloat("SequenceLength");
        int numFrames = properties.GetInt("NumFrames");

        var translationFormat = ParseCompressionFormat(properties.GetName("TranslationCompressionFormat"), AnimationCompressionFormat.None);
        var rotationFormat = ParseCompressionFormat(properties.GetName("RotationCompressionFormat"), AnimationCompressionFormat.Float96NoW);
        var keyFormat = ParseKeyFormat(properties.GetName("KeyEncodingFormat"));

        PropertyTag? offsetsTag = properties.Find("CompressedTrackOffsets");
        if (offsetsTag is null) return null;
        int[] trackOffsets = DecodeIntArray(offsetsTag);

        ReadOnlySpan<byte> compressed = LocateCompressedStream(package.GetExportData(exportIndex), properties.PayloadOffset);
        if (compressed.IsEmpty) return null;

        int trackCount = Math.Min(trackBoneNames.Count, trackOffsets.Length / 4);

        var tracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < trackCount; i++)
        {
            string boneName = trackBoneNames[i];
            if (string.IsNullOrWhiteSpace(boneName)) continue;

            int transOffset = trackOffsets[(i * 4) + 0];
            int transNumKeys = trackOffsets[(i * 4) + 1];
            int rotOffset = trackOffsets[(i * 4) + 2];
            int rotNumKeys = trackOffsets[(i * 4) + 3];

            List<BonePositionKey> positionKeys = DecodePositionTrack(
                compressed, transOffset, transNumKeys, translationFormat, keyFormat, numFrames);
            List<BoneRotationKey> rotationKeys = DecodeRotationTrack(
                compressed, rotOffset, rotNumKeys, rotationFormat, keyFormat, numFrames);

            if (positionKeys.Count == 0 && rotationKeys.Count == 0) continue;

            tracks.TryAdd(boneName, new BoneTrack { PositionKeys = positionKeys, RotationKeys = rotationKeys });
        }

        if (tracks.Count == 0) return null;

        return new BoneAnimation { Name = sequenceName, DurationSeconds = sequenceLength, Tracks = tracks };
    }

    /// <summary>
    /// The binary tail after an AnimSequence's tagged properties: first the
    /// (usually empty, once compression is in use) uncompressed track array,
    /// then the compressed byte stream — each a standard length-prefixed array.
    /// </summary>
    private static ReadOnlySpan<byte> LocateCompressedStream(ReadOnlySpan<byte> data, int payloadOffset)
    {
        var cursor = new PackageCursor(data, payloadOffset);

        int rawTrackCount = cursor.ReadInt32();
        if (rawTrackCount < 0 || rawTrackCount > 100_000) return [];

        for (int i = 0; i < rawTrackCount; i++)
        {
            int posCount = cursor.ReadInt32();
            if (posCount < 0 || (long)posCount * 12 > cursor.Remaining) return [];
            cursor.Skip(posCount * 12);

            int rotCount = cursor.ReadInt32();
            if (rotCount < 0 || (long)rotCount * 16 > cursor.Remaining) return [];
            cursor.Skip(rotCount * 16);
        }

        int compressedCount = cursor.ReadInt32();
        if (compressedCount < 0 || compressedCount > cursor.Remaining) return [];

        return cursor.ReadBytes(compressedCount);
    }

    // --- Track decoding -------------------------------------------------

    internal static List<BonePositionKey> DecodePositionTrack(
        ReadOnlySpan<byte> stream, int offset, int numKeys, AnimationCompressionFormat format,
        AnimationKeyFormat keyFormat, int numFrames)
    {
        var keys = new List<BonePositionKey>();
        if (numKeys <= 0 || offset < 0 || offset >= stream.Length) return keys;

        var cursor = new PackageCursor(stream, offset);
        AnimationCompressionFormat effective = numKeys == 1 ? AnimationCompressionFormat.None : format;

        System.Numerics.Vector3[] values;
        List<float> times;
        try
        {
            values = effective switch
            {
                AnimationCompressionFormat.IntervalFixed32NoW => ReadIntervalFixedVectors(ref cursor, numKeys),
                _ => ReadFloatVectors(ref cursor, numKeys), // ACF_None and anything else this tool decodes: 3 raw floats
            };
            times = DecodeKeyTimes(ref cursor, numKeys, keyFormat, numFrames);
        }
        catch (InvalidPackageException ex)
        {
            Console.Error.WriteLine($"    (position track at {offset}, {numKeys} keys, {format}: {ex.Message})");
            return keys;
        }

        for (int i = 0; i < values.Length && i < times.Count; i++)
            keys.Add(new BonePositionKey(times[i], values[i]));

        return keys;
    }

    internal static List<BoneRotationKey> DecodeRotationTrack(
        ReadOnlySpan<byte> stream, int offset, int numKeys, AnimationCompressionFormat format,
        AnimationKeyFormat keyFormat, int numFrames)
    {
        var keys = new List<BoneRotationKey>();
        if (numKeys <= 0 || offset < 0 || offset >= stream.Length) return keys;

        var cursor = new PackageCursor(stream, offset);
        // A track with a single key is always stored as three floats with W
        // derived, regardless of the sequence's declared rotation format.
        AnimationCompressionFormat effective = numKeys == 1 && format != AnimationCompressionFormat.None
            ? AnimationCompressionFormat.Float96NoW
            : format;

        System.Numerics.Quaternion[] values;
        List<float> times;
        try
        {
            values = effective switch
            {
                AnimationCompressionFormat.None => ReadExplicitQuaternions(ref cursor, numKeys),
                AnimationCompressionFormat.Float96NoW => ReadDerivedWQuaternions(ref cursor, numKeys),
                AnimationCompressionFormat.IntervalFixed32NoW => ReadIntervalFixedQuaternions(ref cursor, numKeys),
                _ => [],
            };

            if (values.Length == 0) return keys;

            // The engine's compressed rotation tracks store each key as the
            // conjugate of the rotation this tool needs — confirmed by
            // comparing baked output against a known-good export: X/Y/Z came
            // out exactly negated with W untouched at every key, on every
            // bone, while translation matched perfectly. Uncompressed bind-
            // pose orientations (read straight from the mesh, not through
            // this path) need no such correction, which is why this wasn't
            // visible until an actual animated clip was checked frame by
            // frame.
            //
            // Conditional on the format, not applied unconditionally: this
            // is specifically a quirk of the engine's own compressed
            // encoding. ACF_None is explicit, unambiguous storage — exactly
            // what AnimSequenceEncoder writes — and applying this correction
            // to it flips every rotation to its own inverse instead of
            // leaving it alone. Caught by EncoderRoundTripVerifier: an
            // always-exactly-180° error across every sequence, worst on
            // whichever bone's own rotation happened to be closest to a
            // quarter turn — exactly what conjugating a quaternion against
            // itself produces, and a dead giveaway once seen.
            if (effective != AnimationCompressionFormat.None)
            {
                for (int i = 0; i < values.Length; i++)
                    values[i] = new System.Numerics.Quaternion(-values[i].X, -values[i].Y, -values[i].Z, values[i].W);
            }

            // Each key's W was derived independently as the positive square
            // root, with no memory of the key before it — harmless for a
            // bone that barely rotates frame to frame, but for one that
            // sweeps through a wide arc (an IK effector reaching for
            // something, say) that can flip a key onto the opposite side of
            // the quaternion's double cover from its neighbor. Slerping
            // between two keys like that takes the long way around instead
            // of the short way, landing near-perpendicular to the intended
            // rotation at the midpoint — confirmed by a round-trip check
            // landing suspiciously close to a clean 180° off on exactly the
            // bones with the most dramatic per-frame rotation swings.
            // Flipping a key's sign as a whole doesn't change which rotation
            // it represents, just which of its two equally-valid
            // representations gets used, so this only ever helps
            // interpolation and never changes a single key's own meaning.
            for (int i = 1; i < values.Length; i++)
            {
                if (System.Numerics.Quaternion.Dot(values[i - 1], values[i]) < 0f)
                    values[i] = new System.Numerics.Quaternion(-values[i].X, -values[i].Y, -values[i].Z, -values[i].W);
            }

            times = DecodeKeyTimes(ref cursor, numKeys, keyFormat, numFrames);
        }
        catch (InvalidPackageException ex)
        {
            Console.Error.WriteLine($"    (rotation track at {offset}, {numKeys} keys, {format}: {ex.Message})");
            return keys;
        }

        for (int i = 0; i < values.Length && i < times.Count; i++)
            keys.Add(new BoneRotationKey(times[i], values[i]));

        return keys;
    }

    private static System.Numerics.Vector3[] ReadFloatVectors(ref PackageCursor cursor, int numKeys)
    {
        var values = new System.Numerics.Vector3[numKeys];
        for (int i = 0; i < numKeys; i++)
            values[i] = new System.Numerics.Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        return values;
    }

    /// <summary>
    /// ACF_IntervalFixed32NoW: six floats (per-axis min then max), then one
    /// packed uint32 per key — 11 bits X, 11 bits Y, 10 bits Z, each scaled
    /// back into its axis's [min, max] range. The bit split matches the
    /// packed-position scheme already confirmed for mesh vertices; this is
    /// the general published UE3 scheme for this format, not something
    /// separately verified here.
    /// </summary>
    private static System.Numerics.Vector3[] ReadIntervalFixedVectors(ref PackageCursor cursor, int numKeys)
    {
        var min = new System.Numerics.Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        var max = new System.Numerics.Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        System.Numerics.Vector3 range = max - min;

        var values = new System.Numerics.Vector3[numKeys];
        for (int i = 0; i < numKeys; i++)
        {
            uint packed = cursor.ReadUInt32();
            int x = (int)(packed & 0x7FF);
            int y = (int)((packed >> 11) & 0x7FF);
            int z = (int)((packed >> 22) & 0x3FF);

            values[i] = new System.Numerics.Vector3(
                min.X + (x / 2047.0f * range.X),
                min.Y + (y / 2047.0f * range.Y),
                min.Z + (z / 1023.0f * range.Z));
        }

        return values;
    }

    private static System.Numerics.Quaternion[] ReadExplicitQuaternions(ref PackageCursor cursor, int numKeys)
    {
        var values = new System.Numerics.Quaternion[numKeys];
        for (int i = 0; i < numKeys; i++)
            values[i] = new System.Numerics.Quaternion(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        return values;
    }

    /// <summary>ACF_Float96NoW: three floats; W is derived so the result is a valid unit quaternion.</summary>
    private static System.Numerics.Quaternion[] ReadDerivedWQuaternions(ref PackageCursor cursor, int numKeys)
    {
        var values = new System.Numerics.Quaternion[numKeys];
        for (int i = 0; i < numKeys; i++)
        {
            float x = cursor.ReadSingle(), y = cursor.ReadSingle(), z = cursor.ReadSingle();
            float lengthSquared = (x * x) + (y * y) + (z * z);
            float w = lengthSquared < 1f ? MathF.Sqrt(1f - lengthSquared) : 0f;
            values[i] = new System.Numerics.Quaternion(x, y, z, w);
        }
        return values;
    }

    /// <summary>The rotation counterpart of <see cref="ReadIntervalFixedVectors"/>: X/Y/Z packed and ranged the same way, W derived.</summary>
    private static System.Numerics.Quaternion[] ReadIntervalFixedQuaternions(ref PackageCursor cursor, int numKeys)
    {
        var min = new System.Numerics.Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        var max = new System.Numerics.Vector3(cursor.ReadSingle(), cursor.ReadSingle(), cursor.ReadSingle());
        System.Numerics.Vector3 range = max - min;

        var values = new System.Numerics.Quaternion[numKeys];
        for (int i = 0; i < numKeys; i++)
        {
            uint packed = cursor.ReadUInt32();
            int xi = (int)(packed & 0x7FF);
            int yi = (int)((packed >> 11) & 0x7FF);
            int zi = (int)((packed >> 22) & 0x3FF);

            float x = min.X + (xi / 2047.0f * range.X);
            float y = min.Y + (yi / 2047.0f * range.Y);
            float z = min.Z + (zi / 1023.0f * range.Z);
            float lengthSquared = (x * x) + (y * y) + (z * z);
            float w = lengthSquared < 1f ? MathF.Sqrt(1f - lengthSquared) : 0f;

            values[i] = new System.Numerics.Quaternion(x, y, z, w);
        }

        return values;
    }

    /// <summary>
    /// A track's per-key time, as a frame index: implicit and uniform for
    /// AKF_ConstantKeyLerp, an explicit byte-or-word array for
    /// AKF_VariableKeyLerp. Kept as a raw frame number rather than converted
    /// to seconds — see the frame-number convention note on
    /// <see cref="BonePositionKey"/>/<see cref="BoneRotationKey"/>.
    /// </summary>
    private static List<float> DecodeKeyTimes(ref PackageCursor cursor, int numKeys, AnimationKeyFormat keyFormat, int numFrames)
    {
        var times = new List<float>(numKeys);

        if (numKeys <= 1)
        {
            if (numKeys == 1) times.Add(0f);
            return times;
        }

        if (keyFormat == AnimationKeyFormat.ConstantKeyLerp)
        {
            // No time array is stored: keys sit at uniform frame indices
            // [0, maxFrame/N, 2*maxFrame/N, ..., maxFrame] across the sequence.
            float maxFrame = Math.Max(1, numFrames - 1);
            for (int i = 0; i < numKeys; i++)
            {
                float fraction = numKeys > 1 ? (float)i / (numKeys - 1) : 0f;
                times.Add(maxFrame * fraction);
            }

            return times;
        }

        // AKF_VariableKeyLerp: an explicit per-key frame index, byte-wide
        // unless the sequence has more than 255 frames, 4-byte aligned front
        // and back.
        AlignTo4(ref cursor);
        bool useWord = numFrames > 0xFF;

        for (int i = 0; i < numKeys; i++)
        {
            float frame = useWord ? cursor.ReadUInt16() : cursor.ReadByte();
            times.Add(frame);
        }

        AlignTo4(ref cursor);
        return times;
    }

    private static void AlignTo4(ref PackageCursor cursor)
    {
        int remainder = cursor.Position % 4;
        if (remainder != 0) cursor.Skip(4 - remainder);
    }

    // --- Property array decoding -----------------------------------------

    private static int[] DecodeIntArray(PropertyTag tag)
    {
        ReadOnlySpan<byte> value = tag.Value.Span;
        if (value.Length < 4) return [];

        int count = BitConverter.ToInt32(value);
        if (count < 0 || 4 + ((long)count * 4) > value.Length) return [];

        var result = new int[count];
        for (int i = 0; i < count; i++)
            result[i] = BitConverter.ToInt32(value.Slice(4 + (i * 4), 4));

        return result;
    }

    private static List<string> DecodeNameArray(PropertyTag tag, NameTable names)
    {
        var result = new List<string>();
        ReadOnlySpan<byte> value = tag.Value.Span;
        if (value.Length < 4) return result;

        int count = BitConverter.ToInt32(value);
        if (count < 0 || 4 + ((long)count * 8) > value.Length) return result;

        for (int i = 0; i < count; i++)
        {
            int at = 4 + (i * 8);
            int index = BitConverter.ToInt32(value.Slice(at, 4));
            int number = BitConverter.ToInt32(value.Slice(at + 4, 4));
            result.Add((uint)index < (uint)names.Count ? names.Resolve(index, number) : string.Empty);
        }

        return result;
    }

    private static List<ObjectReference> DecodeObjectArray(PropertyTag tag)
    {
        var result = new List<ObjectReference>();
        ReadOnlySpan<byte> value = tag.Value.Span;
        if (value.Length < 4) return result;

        int count = BitConverter.ToInt32(value);
        if (count < 0 || 4 + ((long)count * 4) > value.Length) return result;

        for (int i = 0; i < count; i++)
            result.Add(new ObjectReference(BitConverter.ToInt32(value.Slice(4 + (i * 4), 4))));

        return result;
    }

    private static AnimationCompressionFormat ParseCompressionFormat(string name, AnimationCompressionFormat defaultFormat)
    {
        // UE3 omits a property from the tag stream entirely when it equals its
        // class default, so an empty/unresolved name here means "not stored",
        // not "unrecognized" — the field's real default applies.
        if (string.IsNullOrEmpty(name)) return defaultFormat;

        return name.ToUpperInvariant() switch
        {
            "ACF_NONE" => AnimationCompressionFormat.None,
            "ACF_FLOAT96NOW" => AnimationCompressionFormat.Float96NoW,
            "ACF_INTERVALFIXED32NOW" => AnimationCompressionFormat.IntervalFixed32NoW,
            "ACF_IDENTITY" => AnimationCompressionFormat.Identity,
            _ => AnimationCompressionFormat.Unsupported,
        };
    }

    private static AnimationKeyFormat ParseKeyFormat(string name)
    {
        // Same reasoning as above: AKF_ConstantKeyLerp (0) is the class
        // default, so an absent property means constant-key, not unsupported.
        if (string.IsNullOrEmpty(name)) return AnimationKeyFormat.ConstantKeyLerp;

        return name.ToUpperInvariant() switch
        {
            "AKF_CONSTANTKEYLERP" => AnimationKeyFormat.ConstantKeyLerp,
            "AKF_VARIABLEKEYLERP" => AnimationKeyFormat.VariableKeyLerp,
            _ => AnimationKeyFormat.Unsupported, // notably AKF_PerTrackCompression — a materially different, per-track-custom scheme this tool does not decode
        };
    }
}

public enum AnimationCompressionFormat
{
    None,
    Float96NoW,
    IntervalFixed32NoW,
    Identity,
    Unsupported,
}

public enum AnimationKeyFormat
{
    ConstantKeyLerp,
    VariableKeyLerp,
    Unsupported,
}
