using System.Numerics;

namespace MhoExtendedModManager.Fx;

/// <summary>
/// A skeletal mesh's sockets (power effects spawn at them: Thor's hand, a weapon's tip): SkeletalMeshSocket objects in the
/// mesh's Sockets array, each a SocketName on a BoneName at RelativeLocation / RelativeRotation (the MHO Hero Creator's
/// HeroMesh.Sockets). A socket's place in a pose = its local matrix × its bone's posed matrix.
/// </summary>
static class FxSockets
{
    /// <summary>Name → (bone, local matrix) for the mesh <paramref name="meshName"/> in a package; empty when it has none.</summary>
    public static Dictionary<string, (string Bone, Matrix4x4 Local)> Of(string packageFile, string meshName)
    {
        var d = new Dictionary<string, (string, Matrix4x4)>(StringComparer.OrdinalIgnoreCase);
        FxPkg p;
        try { p = FxPkg.Open(packageFile); } catch (Exception ex) when (ex is IOException or InvalidDataException or MhoPackageModifier.PackageFormatException) { return d; }
        int mesh = Enumerable.Range(0, p.T.Exports.Count).FirstOrDefault(i => p.T.Exports[i].ObjectName.Equals(meshName, StringComparison.OrdinalIgnoreCase)
            && p.T.ClassOf(p.T.Exports[i]).Equals("SkeletalMesh", StringComparison.OrdinalIgnoreCase), -1);
        if (mesh < 0) return d;
        var sockets = FxProps.Find(p.Bytes, p.T, p.T.Exports[mesh])?.Props.FirstOrDefault(x => x.Name.Equals("Sockets", StringComparison.OrdinalIgnoreCase));
        if (sockets == null) return d;
        int count = BitConverter.ToInt32(p.Bytes, sockets.ValueAt);
        if (count < 0 || sockets.Size != 4 + 4 * count) return d;
        for (int k = 0; k < count; k++)
        {
            int r = BitConverter.ToInt32(p.Bytes, sockets.ValueAt + 4 + 4 * k);
            if (r <= 0) continue;
            var props = FxProps.Find(p.Bytes, p.T, p.T.Exports[r - 1])?.Props;
            if (props == null) continue;
            string? name = props.FirstOrDefault(x => x.Name.Equals("SocketName", StringComparison.OrdinalIgnoreCase))?.Value;
            string? bone = props.FirstOrDefault(x => x.Name.Equals("BoneName", StringComparison.OrdinalIgnoreCase))?.Value;
            if (name == null || bone == null) continue;
            var loc = props.FirstOrDefault(x => x.Name.Equals("RelativeLocation", StringComparison.OrdinalIgnoreCase));
            var rot = props.FirstOrDefault(x => x.Name.Equals("RelativeRotation", StringComparison.OrdinalIgnoreCase));
            Vector3 l = loc == null ? Vector3.Zero : new(BitConverter.ToSingle(p.Bytes, loc.ValueAt), BitConverter.ToSingle(p.Bytes, loc.ValueAt + 4), BitConverter.ToSingle(p.Bytes, loc.ValueAt + 8));
            var m = rot == null ? Matrix4x4.Identity : RotationMatrix(BitConverter.ToInt32(p.Bytes, rot.ValueAt), BitConverter.ToInt32(p.Bytes, rot.ValueAt + 4), BitConverter.ToInt32(p.Bytes, rot.ValueAt + 8));
            m.M41 = l.X; m.M42 = l.Y; m.M43 = l.Z;
            d[name] = (bone, m);
        }
        return d;
    }

    /// <summary>UE3's FRotationMatrix (rows = the X, Y, Z axes) for a rotator (65536 = a turn).</summary>
    public static Matrix4x4 RotationMatrix(int pitch, int yaw, int roll)
    {
        double R(int u) => u * Math.PI / 32768;
        float SP = (float)Math.Sin(R(pitch)), CP = (float)Math.Cos(R(pitch)), SY = (float)Math.Sin(R(yaw)), CY = (float)Math.Cos(R(yaw)), SR = (float)Math.Sin(R(roll)), CR = (float)Math.Cos(R(roll));
        return new Matrix4x4(
            CP * CY, CP * SY, SP, 0,
            SR * SP * CY - CR * SY, SR * SP * SY + CR * CY, -SR * CP, 0,
            -(CR * SP * CY + SR * SY), CY * SR - CR * SP * SY, CR * CP, 0,
            0, 0, 0, 1);
    }
}
