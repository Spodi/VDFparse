using System.Collections.Immutable;
using System.Diagnostics;
using System.Text.Json;

namespace VDFparse;

public class VDFTransformer : IDisposable
{
    public BinaryReader Reader { get; private set; }
    public Utf8JsonWriter Writer { get; private set; }
    public bool InformationOnly { get; init; }
    private bool disposed;

    public VDFTransformer(
        Stream input,
        Stream output,
        JsonWriterOptions options = default,
        bool infoOnly = false
    )
    {
        Reader = new BinaryReader(input);
        Writer = new Utf8JsonWriter(output, options);
        InformationOnly = infoOnly;
    }

    public void Transform() => Transform(null);

    public void Transform(HashSet<uint>? ids)
    {
        var magic = Reader.ReadUInt32();

        var type = magic switch
        {
            0x07_56_44_27 => TransformationType.AppInfoV1,
            0x07_56_44_28 => TransformationType.AppInfoV2,
            0x06_56_55_27 => TransformationType.PackageInfoV1,
            0x06_56_55_28 => TransformationType.PackageInfoV2,
            _ => throw new InvalidDataException($"Unknown header: {magic:X8}"),
        };
        var endMarker = type switch
        {
            TransformationType.AppInfoV1 or TransformationType.AppInfoV2 => 0u,
            TransformationType.PackageInfoV1 or TransformationType.PackageInfoV2 => ~0u,
            _ => throw new UnreachableException($"{nameof(type)} was checked before"),
        };

        Writer.WriteStartObject();
        Writer.WriteString("magic"u8, $"0x{magic:X8}");
        Writer.WriteString(
            "e_universe"u8,
            Reader.ReadUInt32() switch
            {
                0 => "invalid"u8,
                1 => "public"u8,
                2 => "beta"u8,
                3 => "internal"u8,
                4 => "dev"u8,
                5 => "max"u8,
                _ => throw new InvalidDataException("Invalid value for EUniverse"),
            }
        );

        Writer.WriteStartArray("datasets"u8);
        while (true)
        {
            var id = Reader.ReadUInt32();
            if (id == endMarker)
            {
                break;
            }

            if (ids is not null && !ids.Contains(id))
            {
                ConsumeSingleData(type);
            }
            else
            {
                TransformSingleData(id, type);
            }
        }
        Writer.WriteEndArray();

        Writer.WriteEndObject();
    }

    // Data structure for a single VDF dataset entry is:
    // AppInfo:
    //  - u32   id      // Already read
    //  - u32   size    // Use this to skip block
    //
    // PackageInfo:
    //  - u32    4  id              // Already read
    //  - 20b   20  hash
    //  - u32    4  change_number
    //  - u64    8  picsToken       // Only V2
    //  - ...       binary vdf
    private void ConsumeSingleData(TransformationType type)
    {
        switch (type)
        {
            case TransformationType.AppInfoV1:
            case TransformationType.AppInfoV2:
                Reader.ReadBytes(Reader.ReadInt32());
                break;

            case TransformationType.PackageInfoV1:
                Reader.ReadBytes(20 + 4);
                KVTransformer.Consume(Reader);
                break;

            case TransformationType.PackageInfoV2:
                Reader.ReadBytes(20 + 4 + 8);
                KVTransformer.Consume(Reader);
                break;

            default:
                throw new UnreachableException($"{nameof(type)} was checked before");
        }
    }

    private void TransformSingleData(uint id, TransformationType type)
    {
        Writer.WriteStartObject();
        Writer.WriteNumber("id"u8, id);
        if (type is TransformationType.AppInfoV1 or TransformationType.AppInfoV2)
        {
            Writer.WriteNumber("size"u8, Reader.ReadUInt32());
            Writer.WriteNumber("info_state"u8, Reader.ReadUInt32());
            Writer.WriteString(
                "last_updated"u8,
                DateTimeOffset.FromUnixTimeSeconds(Reader.ReadUInt32()).DateTime
            );
            Writer.WriteNumber("token"u8, Reader.ReadUInt64());
        }

        Writer.WriteBase64String("hash"u8, Reader.ReadBytes(20));
        if (type == TransformationType.PackageInfoV2)
        {
            Writer.WriteNumber("token"u8, Reader.ReadUInt64());
        }

        Writer.WriteNumber("change_number"u8, Reader.ReadUInt32());

        if (type == TransformationType.AppInfoV2)
        {
            Writer.WriteBase64String("vdf_hash"u8, Reader.ReadBytes(20));
        }

        if (!InformationOnly)
        {
            KVTransformer.Transform(Reader, Writer);
        }
        else
        {
            KVTransformer.Consume(Reader);
        }

        Writer.WriteEndObject();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                Reader.Dispose();
                Writer.Dispose();
            }

            Reader = null!;
            Writer = null!;
            disposed = true;
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }
}

enum TransformationType
{
    AppInfoV1,
    AppInfoV2,
    PackageInfoV1,
    PackageInfoV2,
}
