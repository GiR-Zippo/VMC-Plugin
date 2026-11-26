using System.Buffers.Binary;

namespace XivBoneServer;

public enum PacketType : uint { None = 0, FinalBones = 1, Heartbeat = 2 }
public readonly record struct CellKey(uint Map, uint World, int Cx, int Cz);

public readonly record struct WireHeader(
    ushort Flags, ulong ClientId, uint Map, uint World, PacketType Type, uint PayloadLength)
{
    public const int Size = 32;
    public const uint Magic = 0x58495642; // "XIVB"
    public const ushort Version = 1;

    public static bool TryRead(ReadOnlySpan<byte> s, out WireHeader h)
    {
        h = default;
        if (s.Length < Size) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(s) != Magic) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(s[4..]) != Version) return false;
        h = new WireHeader(
            BinaryPrimitives.ReadUInt16LittleEndian(s[6..]),
            BinaryPrimitives.ReadUInt64LittleEndian(s[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
            (PacketType)BinaryPrimitives.ReadUInt32LittleEndian(s[24..]),
            BinaryPrimitives.ReadUInt32LittleEndian(s[28..]));
        return true;
    }
}