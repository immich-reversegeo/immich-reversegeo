using System;
using System.IO;

namespace ImmichReverseGeo.Gadm.Services;

public static class GadmDataAccess
{
    public static (byte[] Wkb, double? XMin, double? YMin, double? XMax, double? YMax) ReadGeoPackageGeometry(byte[] blob)
    {
        if (blob.Length < 8)
        {
            throw new InvalidDataException("GeoPackage geometry blob is too short.");
        }

        if (blob[0] != (byte)'G' || blob[1] != (byte)'P')
        {
            throw new InvalidDataException("GeoPackage geometry blob is missing the GP magic header.");
        }

        var flags = blob[3];
        var envelopeIndicator = (flags >> 1) & 0x07;
        var littleEndian = (flags & 0x01) == 1;

        var envelopeBytes = envelopeIndicator switch
        {
            0 => 0,
            1 => 32,
            2 => 48,
            3 => 48,
            4 => 64,
            _ => throw new InvalidDataException($"Unsupported GeoPackage envelope indicator '{envelopeIndicator}'.")
        };

        var headerBytes = 8 + envelopeBytes;
        if (blob.Length <= headerBytes)
        {
            throw new InvalidDataException("GeoPackage geometry blob does not contain WKB content.");
        }

        double? xmin = null;
        double? ymin = null;
        double? xmax = null;
        double? ymax = null;

        if (envelopeBytes >= 32)
        {
            xmin = ReadDouble(blob, 8, littleEndian);
            xmax = ReadDouble(blob, 16, littleEndian);
            ymin = ReadDouble(blob, 24, littleEndian);
            ymax = ReadDouble(blob, 32, littleEndian);
        }

        var wkb = new byte[blob.Length - headerBytes];
        Buffer.BlockCopy(blob, headerBytes, wkb, 0, wkb.Length);
        return (wkb, xmin, ymin, xmax, ymax);
    }

    private static double ReadDouble(byte[] buffer, int offset, bool littleEndian)
    {
        Span<byte> bytes = stackalloc byte[sizeof(double)];
        buffer.AsSpan(offset, sizeof(double)).CopyTo(bytes);
        if (BitConverter.IsLittleEndian != littleEndian)
        {
            bytes.Reverse();
        }

        return BitConverter.ToDouble(bytes);
    }
}
