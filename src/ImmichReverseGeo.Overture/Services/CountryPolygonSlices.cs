using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using NetTopologySuite.Geometries;

namespace ImmichReverseGeo.Overture.Services;

// Keep byte ranges into the original WKB, including each polygon's holes and doubles.
// No geometry/Coordinate object graph is needed to prepare the disk index.
internal static class CountryPolygonSlices
{
    internal sealed record Slice(int Offset, int Length, Envelope Bounds);

    public static IReadOnlyList<Slice> Read(byte[] wkb, Envelope fallbackBounds, CancellationToken ct)
    {
        var slices = new List<Slice>();
        var scanner = new Scanner(wkb, ct);
        if (!scanner.ReadGeometry(slices, 0))
        {
            // Non-polygon WKB remains usable through the original geometry predicate.
            return [new Slice(0, wkb.Length, fallbackBounds)];
        }
        if (scanner.Position != wkb.Length)
        {
            throw new InvalidDataException("Country WKB has trailing or unsupported data.");
        }
        return slices;
    }

    private sealed class Scanner(byte[] bytes, CancellationToken ct)
    {
        public int Position { get; private set; }

        public bool ReadGeometry(List<Slice> slices, int depth)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 32)
            {
                throw new InvalidDataException("Country WKB nesting is too deep.");
            }
            var start = Position;
            RequireBytes(1);
            var order = bytes[Position++];
            if (order > 1)
            {
                throw new InvalidDataException("Invalid WKB byte order.");
            }
            var littleEndian = order == 1;
            var encodedType = UInt32(littleEndian);
            var type = encodedType & 0x1fffffff;
            var dimensions = 2 + ((encodedType & 0x80000000) != 0 ? 1 : 0) + ((encodedType & 0x40000000) != 0 ? 1 : 0);
            if ((encodedType & 0x20000000) != 0)
            {
                _ = UInt32(littleEndian); // EWKB SRID, preserved in each original slice.
            }
            if (type >= 1000)
            {
                var dimensionalType = type / 1000;
                dimensions = dimensionalType == 3 ? 4 : dimensionalType is 1 or 2 ? 3 : throw new InvalidDataException("Unsupported WKB dimension.");
                type %= 1000;
            }
            if (type == 6)
            {
                var count = Count(littleEndian);
                for (var index = 0; index < count; index++)
                {
                    if (!ReadGeometry(slices, depth + 1))
                    {
                        return false;
                    }
                }
                return true;
            }
            if (type != 3)
            {
                return false;
            }
            var bounds = new Envelope();
            var rings = Count(littleEndian);
            for (var ring = 0; ring < rings; ring++)
            {
                var points = Count(littleEndian);
                for (var point = 0; point < points; point++)
                {
                    if ((point & 1023) == 0)
                    {
                        ct.ThrowIfCancellationRequested();
                    }
                    var x = Double(littleEndian);
                    var y = Double(littleEndian);
                    if (!double.IsFinite(x) || !double.IsFinite(y))
                    {
                        throw new InvalidDataException("Country WKB contains a non-finite coordinate.");
                    }
                    bounds.ExpandToInclude(x, y);
                    RequireBytes((dimensions - 2) * sizeof(double));
                    Position += (dimensions - 2) * sizeof(double);
                }
            }
            if (!bounds.IsNull)
            {
                slices.Add(new Slice(start, Position - start, bounds));
            }
            return true;
        }

        private int Count(bool littleEndian)
        {
            var count = UInt32(littleEndian);
            if (count > bytes.Length)
            {
                throw new InvalidDataException("Invalid WKB element count.");
            }
            return (int)count;
        }

        private uint UInt32(bool littleEndian)
        {
            RequireBytes(sizeof(uint));
            var value = littleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(Position)) : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(Position));
            Position += sizeof(uint);
            return value;
        }

        private double Double(bool littleEndian)
        {
            RequireBytes(sizeof(double));
            var value = littleEndian ? BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(Position)) : BinaryPrimitives.ReadDoubleBigEndian(bytes.AsSpan(Position));
            Position += sizeof(double);
            return value;
        }

        private void RequireBytes(int count)
        {
            if (count < 0 || Position > bytes.Length - count)
            {
                throw new InvalidDataException("Truncated country WKB.");
            }
        }
    }
}
