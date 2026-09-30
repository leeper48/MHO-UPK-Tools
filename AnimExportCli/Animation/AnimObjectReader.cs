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

    /// <param name="RotationOnly">UE3's bAnimRotationOnly (true unless stored: defaults are omitted): the animation drives bone
    /// rotations; positions come from the mesh's own bind pose, except for the bones in <paramref name="TranslationBones"/>.</param>
    public sealed record AnimSetInfo(int ExportIndex, IReadOnlyList<string> TrackBoneNames, IReadOnlyList<ObjectReference> Sequences,
        bool RotationOnly = true, IReadOnlyList<string>? TranslationBones = null);

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

            PropertyTag? translationTag = properties.Find("UseTranslationBoneNames");
            bool rotationOnly = properties.Find("bAnimRotationOnly") is null || properties.GetBool("bAnimRotationOnly");
            yield return new AnimSetInfo(index, trackBoneNames, DecodeObjectArray(sequencesTag), rotationOnly,
                translationTag is null ? [] : DecodeNameArray(translationTag, package.Names));
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

        if (keyFormat == AnimationKeyFormat.PerTrackCompression)
        {
            DumpPerTrack(compressed, trackOffsets, trackBoneNames, numFrames, output, boneFilter);
            return;
        }

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

        var tracks = new Dictionary<string, BoneTrack>(StringComparer.OrdinalIgnoreCase);

        if (keyFormat == AnimationKeyFormat.PerTrackCompression)
        {
            ReadPerTrackSequence(compressed, trackOffsets, trackBoneNames, numFrames, tracks);
            return tracks.Count == 0 ? null : new BoneAnimation { Name = sequenceName, DurationSeconds = sequenceLength, Tracks = tracks };
        }

        int trackCount = Math.Min(trackBoneNames.Count, trackOffsets.Length / 4);

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
                AnimationCompressionFormat.Fixed48NoW or AnimationCompressionFormat.Fixed32NoW => ReadPackedQuaternions(ref cursor, numKeys, effective),
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

    /// <summary>ACF_Fixed48NoW (3 × uint16) and ACF_Fixed32NoW (11/11/10 bits) rotation keys, all three components, W derived.</summary>
    private static System.Numerics.Quaternion[] ReadPackedQuaternions(ref PackageCursor cursor, int numKeys, AnimationCompressionFormat format)
    {
        var values = new System.Numerics.Quaternion[numKeys];
        for (int i = 0; i < numKeys; i++)
            values[i] = WithW(ReadRotationComponents(ref cursor, format, 7, default, default));
        return values;
    }

    // --- AKF_PerTrackCompression ----------------------------------------
    //
    // UE3's per-track codec (found on Punisher's stock sets, 2026-09-30):
    // CompressedTrackOffsets holds TWO ints per track (translation offset,
    // rotation offset; -1 = no data), not four. Each track starts with a
    // uint32 header: bits 28-31 its own AnimationCompressionFormat, bits
    // 24-27 flags (bits 0/1/2 = X/Y/Z stored, bit 3 = a key-time array
    // follows), bits 0-23 the key count. ACF_IntervalFixed32NoW then stores
    // (min, range) float pairs for the stored components; the keys follow
    // with only the stored components (missing ones are 0); then, with the
    // time flag, the stream is aligned to 4 and a byte (uint16 when the
    // sequence has more than 255 frames) frame index per key, aligned to 4.

    private static void ReadPerTrackSequence(ReadOnlySpan<byte> stream, int[] trackOffsets, IReadOnlyList<string> trackBoneNames,
        int numFrames, Dictionary<string, BoneTrack> tracks)
    {
        int trackCount = Math.Min(trackBoneNames.Count, trackOffsets.Length / 2);
        for (int i = 0; i < trackCount; i++)
        {
            string boneName = trackBoneNames[i];
            if (string.IsNullOrWhiteSpace(boneName)) continue;

            List<BonePositionKey> positionKeys = DecodePerTrackPositions(stream, trackOffsets[i * 2], numFrames);
            List<BoneRotationKey> rotationKeys = DecodePerTrackRotations(stream, trackOffsets[(i * 2) + 1], numFrames);
            if (positionKeys.Count == 0 && rotationKeys.Count == 0) continue;

            tracks.TryAdd(boneName, new BoneTrack { PositionKeys = positionKeys, RotationKeys = rotationKeys });
        }
    }

    /// <summary>
    /// --dump for a per-track sequence: each track's header, and a layout check: every track's data
    /// (header, bounds, keys, times, padding) must end exactly where the next one starts.
    /// </summary>
    private static void DumpPerTrack(ReadOnlySpan<byte> stream, int[] trackOffsets, IReadOnlyList<string> trackBoneNames,
        int numFrames, TextWriter output, string? boneFilter)
    {
        int trackCount = Math.Min(trackBoneNames.Count, trackOffsets.Length / 2);
        output.WriteLine($"  AKF_PerTrackCompression: {trackCount} tracks (trackBoneNames.Count={trackBoneNames.Count}, CompressedTrackOffsets.Length/2={trackOffsets.Length / 2})");

        var starts = trackOffsets.Where(o => o >= 0).Distinct().Order().Append(stream.Length).ToList();
        int layoutErrors = 0;
        var formats = new SortedDictionary<string, int>();

        for (int i = 0; i < trackCount; i++)
        {
            var parts = new List<string>();
            for (int kind = 0; kind < 2; kind++)
            {
                int offset = trackOffsets[(i * 2) + kind];
                string label = kind == 0 ? "trans" : "rot";
                if (PeekPerTrackHeader(stream, offset) is not { } header) { parts.Add($"{label}=none"); continue; }

                string key = $"{label} {header.Format}{(header.HasTimes ? " +times" : string.Empty)}";
                formats[key] = formats.GetValueOrDefault(key) + 1;

                int end = PerTrackEnd(stream, offset, header, numFrames, kind == 1);
                int next = starts.First(s => s > offset);
                string check = end == next ? string.Empty : $" END {end} != NEXT {next}";
                if (end != next) layoutErrors++;
                parts.Add($"{label}@{offset} {header}{check}");
            }

            output.WriteLine($"  [{i}] bone='{trackBoneNames[i]}' {string.Join(" | ", parts)}");

            if (boneFilter is null || !trackBoneNames[i].Contains(boneFilter, StringComparison.OrdinalIgnoreCase)) continue;

            foreach (BonePositionKey k in DecodePerTrackPositions(stream, trackOffsets[i * 2], numFrames))
                output.WriteLine($"      frame={k.TimeFrame,-8:F2} pos=({k.Position.X:F3}, {k.Position.Y:F3}, {k.Position.Z:F3})");
            foreach (BoneRotationKey k in DecodePerTrackRotations(stream, trackOffsets[(i * 2) + 1], numFrames))
                output.WriteLine($"      frame={k.TimeFrame,-8:F2} rot=({k.Rotation.X:F4}, {k.Rotation.Y:F4}, {k.Rotation.Z:F4}, {k.Rotation.W:F4})");
        }

        output.WriteLine($"  Formats: {string.Join(", ", formats.Select(f => $"{f.Key} ×{f.Value}"))}");
        output.WriteLine($"  Layout check: {(layoutErrors == 0 ? "every track ends where the next starts" : $"{layoutErrors} track(s) do NOT end where the next starts")}");
    }

    /// <summary>Where a per-track block ends (after its padding to 4), computed from its header alone.</summary>
    private static int PerTrackEnd(ReadOnlySpan<byte> stream, int offset, PerTrackHeader header, int numFrames, bool rotation)
    {
        int components = ((header.Mask & 1) != 0 ? 1 : 0) + ((header.Mask & 2) != 0 ? 1 : 0) + ((header.Mask & 4) != 0 ? 1 : 0);
        int bounds = header.Format == AnimationCompressionFormat.IntervalFixed32NoW ? components * 8 : 0;
        int keySize = header.Format switch
        {
            AnimationCompressionFormat.None or AnimationCompressionFormat.Float96NoW => components * 4,
            AnimationCompressionFormat.Fixed48NoW => components * 2,
            AnimationCompressionFormat.IntervalFixed32NoW or AnimationCompressionFormat.Fixed32NoW or AnimationCompressionFormat.Float32NoW => 4,
            _ => 0,
        };
        int end = offset + 4 + bounds + (keySize * header.NumKeys);
        end = (end + 3) & ~3;
        if (header.HasTimes && header.NumKeys > 1)
            end = (end + (header.NumKeys * (numFrames > 0xFF ? 2 : 1)) + 3) & ~3;
        return end;
    }

    internal readonly record struct PerTrackHeader(AnimationCompressionFormat Format, int Mask, bool HasTimes, int NumKeys)
    {
        public static PerTrackHeader Parse(uint header) => new(
            (AnimationCompressionFormat)((header >> 28) & 0xF), (int)((header >> 24) & 0x7), ((header >> 24) & 0x8) != 0, (int)(header & 0x00FFFFFF));

        public override string ToString() => $"{Format} mask={Mask}{(HasTimes ? " +times" : string.Empty)} keys={NumKeys}";
    }

    /// <summary>The header of the per-track data at <paramref name="offset"/>, or null when there is none (-1 / out of range).</summary>
    internal static PerTrackHeader? PeekPerTrackHeader(ReadOnlySpan<byte> stream, int offset) =>
        offset < 0 || offset + 4 > stream.Length ? null : PerTrackHeader.Parse(BitConverter.ToUInt32(stream.Slice(offset, 4)));

    private static (System.Numerics.Vector3 Mins, System.Numerics.Vector3 Ranges) ReadPerTrackBounds(ref PackageCursor cursor, PerTrackHeader header)
    {
        System.Numerics.Vector3 mins = default, ranges = default;
        if (header.Format != AnimationCompressionFormat.IntervalFixed32NoW) return (mins, ranges);
        if ((header.Mask & 1) != 0) { mins.X = cursor.ReadSingle(); ranges.X = cursor.ReadSingle(); }
        if ((header.Mask & 2) != 0) { mins.Y = cursor.ReadSingle(); ranges.Y = cursor.ReadSingle(); }
        if ((header.Mask & 4) != 0) { mins.Z = cursor.ReadSingle(); ranges.Z = cursor.ReadSingle(); }
        return (mins, ranges);
    }

    private static List<float> ReadPerTrackTimes(ref PackageCursor cursor, PerTrackHeader header, int numFrames) =>
        DecodeKeyTimes(ref cursor, header.NumKeys,
            header.HasTimes ? AnimationKeyFormat.VariableKeyLerp : AnimationKeyFormat.ConstantKeyLerp, numFrames);

    internal static List<BonePositionKey> DecodePerTrackPositions(ReadOnlySpan<byte> stream, int offset, int numFrames)
    {
        var keys = new List<BonePositionKey>();
        if (PeekPerTrackHeader(stream, offset) is not { } header || header.NumKeys <= 0) return keys;
        if (header.Format == AnimationCompressionFormat.Identity) return keys;

        try
        {
            var cursor = new PackageCursor(stream, offset + 4);
            var (mins, ranges) = ReadPerTrackBounds(ref cursor, header);
            var values = new System.Numerics.Vector3[header.NumKeys];
            for (int i = 0; i < values.Length; i++)
                values[i] = ReadPositionComponents(ref cursor, header.Format, header.Mask, mins, ranges);
            List<float> times = ReadPerTrackTimes(ref cursor, header, numFrames);
            for (int i = 0; i < values.Length && i < times.Count; i++)
                keys.Add(new BonePositionKey(times[i], values[i]));
        }
        catch (InvalidPackageException ex)
        {
            Console.Error.WriteLine($"    (per-track position at {offset}, {header}: {ex.Message})");
            keys.Clear();
        }

        return keys;
    }

    internal static List<BoneRotationKey> DecodePerTrackRotations(ReadOnlySpan<byte> stream, int offset, int numFrames)
    {
        var keys = new List<BoneRotationKey>();
        if (PeekPerTrackHeader(stream, offset) is not { } header || header.NumKeys <= 0) return keys;
        if (header.Format == AnimationCompressionFormat.Identity) return keys;

        try
        {
            var cursor = new PackageCursor(stream, offset + 4);
            var (mins, ranges) = ReadPerTrackBounds(ref cursor, header);
            var values = new System.Numerics.Quaternion[header.NumKeys];
            for (int i = 0; i < values.Length; i++)
            {
                System.Numerics.Quaternion q = WithW(ReadRotationComponents(ref cursor, header.Format, header.Mask, mins, ranges));
                // Engine-compressed data is stored as the conjugate (see DecodeRotationTrack).
                values[i] = new System.Numerics.Quaternion(-q.X, -q.Y, -q.Z, q.W);
                if (i > 0 && System.Numerics.Quaternion.Dot(values[i - 1], values[i]) < 0f)
                    values[i] = -values[i];
            }

            List<float> times = ReadPerTrackTimes(ref cursor, header, numFrames);
            for (int i = 0; i < values.Length && i < times.Count; i++)
                keys.Add(new BoneRotationKey(times[i], values[i]));
        }
        catch (InvalidPackageException ex)
        {
            Console.Error.WriteLine($"    (per-track rotation at {offset}, {header}: {ex.Message})");
            keys.Clear();
        }

        return keys;
    }

    /// <summary>One translation key in a UE3 per-key format; only the components in <paramref name="mask"/> are stored.</summary>
    private static System.Numerics.Vector3 ReadPositionComponents(ref PackageCursor cursor, AnimationCompressionFormat format, int mask,
        System.Numerics.Vector3 mins, System.Numerics.Vector3 ranges)
    {
        switch (format)
        {
            case AnimationCompressionFormat.None:
            case AnimationCompressionFormat.Float96NoW:
                return new System.Numerics.Vector3(
                    (mask & 1) != 0 ? cursor.ReadSingle() : 0f,
                    (mask & 2) != 0 ? cursor.ReadSingle() : 0f,
                    (mask & 4) != 0 ? cursor.ReadSingle() : 0f);
            case AnimationCompressionFormat.Fixed48NoW:
                // uint16 - 255, NOT UE4's (v - 32767) / 32767 × 128. Evidence (2026-09-30, all 33 such tracks in
                // the stock UC__ packages, every one single-key): raw 255 / 257 / 305 / 315 are 0 / 2 / 50 / 60,
                // the values the same bones (Spider-Ham fingers, Black Widow's g_fwd_uprik) have in the other
                // sequences. Only whole numbers were seen, so a finer scale can't be ruled out.
                return new System.Numerics.Vector3(
                    (mask & 1) != 0 ? cursor.ReadUInt16() - 255f : 0f,
                    (mask & 2) != 0 ? cursor.ReadUInt16() - 255f : 0f,
                    (mask & 4) != 0 ? cursor.ReadUInt16() - 255f : 0f);
            case AnimationCompressionFormat.IntervalFixed32NoW:
            {
                // FVectorIntervalFixed32NoW: X low 10 bits, Y next 11, Z top 11.
                uint packed = cursor.ReadUInt32();
                return new System.Numerics.Vector3(
                    (((int)(packed & 0x3FF) - 511) / 511f * ranges.X) + mins.X,
                    (((int)((packed >> 10) & 0x7FF) - 1023) / 1023f * ranges.Y) + mins.Y,
                    (((int)(packed >> 21) - 1023) / 1023f * ranges.Z) + mins.Z);
            }
            default:
                throw new InvalidPackageException($"translation key format {format} is not decoded");
        }
    }

    /// <summary>One rotation key's X/Y/Z in a UE3 per-key format (W is derived by the caller).</summary>
    private static System.Numerics.Vector3 ReadRotationComponents(ref PackageCursor cursor, AnimationCompressionFormat format, int mask,
        System.Numerics.Vector3 mins, System.Numerics.Vector3 ranges)
    {
        switch (format)
        {
            case AnimationCompressionFormat.None:
            case AnimationCompressionFormat.Float96NoW:
                return new System.Numerics.Vector3(
                    (mask & 1) != 0 ? cursor.ReadSingle() : 0f,
                    (mask & 2) != 0 ? cursor.ReadSingle() : 0f,
                    (mask & 4) != 0 ? cursor.ReadSingle() : 0f);
            case AnimationCompressionFormat.Fixed48NoW:
                // FQuatFixed48NoW: uint16, (v - 32767) / 32767; a missing component is 0.
                return new System.Numerics.Vector3(
                    (mask & 1) != 0 ? (cursor.ReadUInt16() - 32767) / 32767f : 0f,
                    (mask & 2) != 0 ? (cursor.ReadUInt16() - 32767) / 32767f : 0f,
                    (mask & 4) != 0 ? (cursor.ReadUInt16() - 32767) / 32767f : 0f);
            case AnimationCompressionFormat.Fixed32NoW:
            case AnimationCompressionFormat.IntervalFixed32NoW:
            {
                // FQuatFixed32NoW / FQuatIntervalFixed32NoW: X top 11 bits, Y next 11, Z low 10.
                uint packed = cursor.ReadUInt32();
                var v = new System.Numerics.Vector3(
                    ((int)(packed >> 21) - 1023) / 1023f,
                    ((int)((packed >> 10) & 0x7FF) - 1023) / 1023f,
                    ((int)(packed & 0x3FF) - 511) / 511f);
                return format == AnimationCompressionFormat.Fixed32NoW ? v : (v * ranges) + mins;
            }
            default:
                throw new InvalidPackageException($"rotation key format {format} is not decoded");
        }
    }

    private static System.Numerics.Quaternion WithW(System.Numerics.Vector3 xyz)
    {
        float lengthSquared = xyz.LengthSquared();
        return new System.Numerics.Quaternion(xyz, lengthSquared < 1f ? MathF.Sqrt(1f - lengthSquared) : 0f);
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
            "ACF_FIXED48NOW" => AnimationCompressionFormat.Fixed48NoW,
            "ACF_FIXED32NOW" => AnimationCompressionFormat.Fixed32NoW,
            "ACF_FLOAT32NOW" => AnimationCompressionFormat.Float32NoW,
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
            "AKF_PERTRACKCOMPRESSION" => AnimationKeyFormat.PerTrackCompression,
            _ => AnimationKeyFormat.Unsupported,
        };
    }
}

/// <summary>UE3's AnimationCompressionFormat; the numeric values are the ones a per-track header stores in its top 4 bits.</summary>
public enum AnimationCompressionFormat
{
    None = 0,
    Float96NoW = 1,
    Fixed48NoW = 2,
    IntervalFixed32NoW = 3,
    Fixed32NoW = 4,
    Float32NoW = 5,
    Identity = 6,
    Unsupported = 100,
}

public enum AnimationKeyFormat
{
    ConstantKeyLerp,
    VariableKeyLerp,
    PerTrackCompression,
    Unsupported,
}
