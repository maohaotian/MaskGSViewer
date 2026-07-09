// SPDX-License-Identifier: MIT
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using GaussianSplatting.Runtime;
using Unity.Mathematics;
using UnityEngine;

public interface ISplatInputAdapter
{
    string DisplayName { get; }
    bool SupportsExtension(string extension);
    long EstimateRuntimeBytes(string filePath);
    RuntimeSplatLoader.SplatData[] ReadFile(string filePath);
}

public static class SplatInputAdapters
{
    const long FallbackEstimateBytes = 100L * 1024 * 1024;

    static readonly object s_lock = new object();
    static readonly List<ISplatInputAdapter> s_adapters = new List<ISplatInputAdapter>
    {
        new PlySplatInputAdapter(),
        new SpzSplatInputAdapter(),
        new SogSplatInputAdapter(),
        new SpxSplatInputAdapter()
    };

    public static void Register(ISplatInputAdapter adapter, bool preferFirst = true)
    {
        if (adapter == null)
            throw new ArgumentNullException(nameof(adapter));

        lock (s_lock)
        {
            s_adapters.RemoveAll(existing => ReferenceEquals(existing, adapter) || existing.GetType() == adapter.GetType());
            if (preferFirst)
                s_adapters.Insert(0, adapter);
            else
                s_adapters.Add(adapter);
        }
    }

    public static bool IsSupportedFileExtension(string filePath)
    {
        return Resolve(filePath) != null;
    }

    public static long EstimateRuntimeBytes(string filePath)
    {
        var adapter = Resolve(filePath);
        if (adapter == null)
            return FallbackEstimateBytes;

        try
        {
            long estimate = adapter.EstimateRuntimeBytes(filePath);
            return estimate > 0 ? estimate : FallbackEstimateBytes;
        }
        catch
        {
            return FallbackEstimateBytes;
        }
    }

    public static RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        var adapter = Resolve(filePath);
        if (adapter == null)
            throw new NotSupportedException($"Unsupported Gaussian splat format: {Path.GetExtension(filePath)}");

        Debug.Log($"[SplatInputAdapters] {Path.GetFileName(filePath)} -> {adapter.DisplayName}");
        return adapter.ReadFile(filePath);
    }

    static ISplatInputAdapter Resolve(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        lock (s_lock)
        {
            for (int i = 0; i < s_adapters.Count; i++)
            {
                if (s_adapters[i].SupportsExtension(ext))
                    return s_adapters[i];
            }
        }

        return null;
    }
}

readonly struct SplatImportOptions
{
    readonly bool _mirrorX;

    SplatImportOptions(bool mirrorX)
    {
        _mirrorX = mirrorX;
    }

    public static SplatImportOptions FromFilePath(string filePath)
    {
        string fileName = Path.GetFileNameWithoutExtension(filePath) ?? string.Empty;
        bool alreadyUnityDx12 = fileName.IndexOf("unity_dx12", StringComparison.OrdinalIgnoreCase) >= 0;
        return new SplatImportOptions(!alreadyUnityDx12);
    }

    public float3 TransformPosition(float3 position)
    {
        return _mirrorX ? GaussianUtils.MirrorPositionX(position) : position;
    }

    public float4 TransformRotation(float4 rotation)
    {
        return _mirrorX ? GaussianUtils.MirrorRotationX(rotation) : rotation;
    }
}

sealed class PlySplatInputAdapter : ISplatInputAdapter
{
    public string DisplayName => "PLY Gaussian Splat";
    public bool SupportsExtension(string extension) => extension == ".ply";

    public long EstimateRuntimeBytes(string filePath)
    {
        return PlyReader.EstimateRuntimeBytes(filePath);
    }

    public RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        return PlyReader.ReadFile(filePath, SplatImportOptions.FromFilePath(filePath));
    }

    static class PlyReader
    {
        enum PlyFormat
        {
            Ascii,
            BinaryLittleEndian,
            BinaryBigEndian
        }

        enum PlyScalarType
        {
            Int8,
            UInt8,
            Int16,
            UInt16,
            Int32,
            UInt32,
            Float32,
            Float64
        }

        sealed class PlyProperty
        {
            public string Name;
            public PlyScalarType Type;
            public int Index;
            public int Offset;
            public int Size;
        }

        sealed class PlyHeader
        {
            public PlyFormat Format;
            public int VertexCount;
            public int VertexStride;
            public List<PlyProperty> Properties = new List<PlyProperty>();
            public Dictionary<string, PlyProperty> PropertyMap = new Dictionary<string, PlyProperty>(StringComparer.OrdinalIgnoreCase);
        }

        sealed class PlySchema
        {
            public PlyProperty X;
            public PlyProperty Y;
            public PlyProperty Z;
            public PlyProperty Red;
            public PlyProperty Green;
            public PlyProperty Blue;
            public PlyProperty Dc0;
            public PlyProperty Dc1;
            public PlyProperty Dc2;
            public PlyProperty Opacity;
            public PlyProperty Scale0;
            public PlyProperty Scale1;
            public PlyProperty Scale2;
            public PlyProperty DirectScaleX;
            public PlyProperty DirectScaleY;
            public PlyProperty DirectScaleZ;
            public PlyProperty Rot0;
            public PlyProperty Rot1;
            public PlyProperty Rot2;
            public PlyProperty Rot3;
            public PlyProperty Qx;
            public PlyProperty Qy;
            public PlyProperty Qz;
            public PlyProperty Qw;
            public PlyProperty[] RestSh = new PlyProperty[45];
        }

        public static long EstimateRuntimeBytes(string filePath)
        {
            var header = ReadHeader(filePath);
            return Math.Max(header.VertexCount, 1) * 236L;
        }

        static PlyHeader ReadHeader(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadHeader(fs);
        }

        public static RuntimeSplatLoader.SplatData[] ReadFile(string filePath, SplatImportOptions options)
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var header = ReadHeader(fs);
            ValidateHeader(header, filePath);

            var schema = BuildSchema(header);
            return header.Format == PlyFormat.Ascii
                ? ReadAsciiVertices(fs, header, schema, options)
                : ReadBinaryVertices(fs, header, schema, options);
        }

        static PlyHeader ReadHeader(FileStream fs)
        {
            var lines = new List<string>();
            var lineBytes = new List<byte>(256);

            while (true)
            {
                int next = fs.ReadByte();
                if (next < 0)
                    throw new Exception("Unexpected end of PLY header");

                if (next == '\n')
                {
                    string line = Encoding.ASCII.GetString(lineBytes.ToArray()).TrimEnd('\r');
                    lines.Add(line);
                    lineBytes.Clear();

                    if (line == "end_header")
                        break;
                }
                else
                {
                    lineBytes.Add((byte)next);
                }

                if (fs.Position > 4 * 1024 * 1024)
                    throw new Exception("PLY header is larger than 4MB; refusing to parse it.");
            }

            return ParseHeaderLines(lines);
        }

        static PlyHeader ParseHeaderLines(List<string> lines)
        {
            if (lines.Count == 0 || lines[0] != "ply")
                throw new Exception("Invalid PLY header: missing 'ply' magic.");

            var header = new PlyHeader();
            bool inVertex = false;
            bool sawFormat = false;

            foreach (string rawLine in lines)
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("comment"))
                    continue;

                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0)
                    continue;

                if (parts[0] == "format")
                {
                    if (parts.Length < 3)
                        throw new Exception("Invalid PLY format line.");
                    header.Format = ParseFormat(parts[1]);
                    sawFormat = true;
                }
                else if (parts[0] == "element")
                {
                    if (parts.Length < 3)
                        throw new Exception("Invalid PLY element line.");
                    inVertex = parts[1] == "vertex";
                    if (inVertex)
                        header.VertexCount = int.Parse(parts[2], CultureInfo.InvariantCulture);
                }
                else if (parts[0] == "property" && inVertex)
                {
                    if (parts.Length >= 2 && parts[1] == "list")
                        throw new NotSupportedException("PLY vertex list properties are not supported for Gaussian splats.");
                    if (parts.Length < 3)
                        throw new Exception("Invalid PLY property line.");

                    var prop = new PlyProperty
                    {
                        Name = parts[2],
                        Type = ParseScalarType(parts[1]),
                        Index = header.Properties.Count,
                        Offset = header.VertexStride
                    };
                    prop.Size = ScalarSize(prop.Type);
                    header.VertexStride += prop.Size;
                    header.Properties.Add(prop);
                    header.PropertyMap[prop.Name] = prop;
                }
            }

            if (!sawFormat)
                throw new Exception("PLY header is missing a format declaration.");

            return header;
        }

        static PlyFormat ParseFormat(string value)
        {
            return value switch
            {
                "ascii" => PlyFormat.Ascii,
                "binary_little_endian" => PlyFormat.BinaryLittleEndian,
                "binary_big_endian" => PlyFormat.BinaryBigEndian,
                _ => throw new NotSupportedException($"Unsupported PLY format: {value}")
            };
        }

        static PlyScalarType ParseScalarType(string value)
        {
            return value switch
            {
                "char" or "int8" => PlyScalarType.Int8,
                "uchar" or "uint8" => PlyScalarType.UInt8,
                "short" or "int16" => PlyScalarType.Int16,
                "ushort" or "uint16" => PlyScalarType.UInt16,
                "int" or "int32" => PlyScalarType.Int32,
                "uint" or "uint32" => PlyScalarType.UInt32,
                "float" or "float32" => PlyScalarType.Float32,
                "double" or "float64" => PlyScalarType.Float64,
                _ => throw new NotSupportedException($"Unsupported PLY scalar type: {value}")
            };
        }

        static int ScalarSize(PlyScalarType type)
        {
            return type switch
            {
                PlyScalarType.Int8 or PlyScalarType.UInt8 => 1,
                PlyScalarType.Int16 or PlyScalarType.UInt16 => 2,
                PlyScalarType.Int32 or PlyScalarType.UInt32 or PlyScalarType.Float32 => 4,
                PlyScalarType.Float64 => 8,
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
            };
        }

        static void ValidateHeader(PlyHeader header, string filePath)
        {
            if (header.VertexCount <= 0)
                throw new Exception($"Invalid PLY vertex count in {filePath}: {header.VertexCount}");
            if (header.Properties.Count == 0)
                throw new Exception($"PLY has no vertex properties: {filePath}");
        }

        static PlySchema BuildSchema(PlyHeader header)
        {
            var map = header.PropertyMap;
            var schema = new PlySchema
            {
                X = Find(map, "x"),
                Y = Find(map, "y"),
                Z = Find(map, "z"),
                Red = Find(map, "red", "r"),
                Green = Find(map, "green", "g"),
                Blue = Find(map, "blue", "b"),
                Dc0 = Find(map, "f_dc_0", "dc_0"),
                Dc1 = Find(map, "f_dc_1", "dc_1"),
                Dc2 = Find(map, "f_dc_2", "dc_2"),
                Opacity = Find(map, "opacity", "alpha", "a"),
                Scale0 = Find(map, "scale_0"),
                Scale1 = Find(map, "scale_1"),
                Scale2 = Find(map, "scale_2"),
                DirectScaleX = Find(map, "scale_x", "sx"),
                DirectScaleY = Find(map, "scale_y", "sy"),
                DirectScaleZ = Find(map, "scale_z", "sz"),
                Rot0 = Find(map, "rot_0"),
                Rot1 = Find(map, "rot_1"),
                Rot2 = Find(map, "rot_2"),
                Rot3 = Find(map, "rot_3"),
                Qx = Find(map, "qx", "quat_x", "rotation_x"),
                Qy = Find(map, "qy", "quat_y", "rotation_y"),
                Qz = Find(map, "qz", "quat_z", "rotation_z"),
                Qw = Find(map, "qw", "quat_w", "rotation_w")
            };

            if (schema.X == null || schema.Y == null || schema.Z == null)
                throw new Exception("PLY must contain x, y, and z vertex properties.");

            for (int i = 0; i < schema.RestSh.Length; i++)
                schema.RestSh[i] = Find(map, $"f_rest_{i}");

            return schema;
        }

        static PlyProperty Find(Dictionary<string, PlyProperty> map, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (map.TryGetValue(names[i], out PlyProperty prop))
                    return prop;
            }

            return null;
        }

        static RuntimeSplatLoader.SplatData[] ReadBinaryVertices(FileStream fs, PlyHeader header, PlySchema schema, SplatImportOptions options)
        {
            long totalVertexBytes = (long)header.VertexCount * header.VertexStride;
            if (totalVertexBytes > int.MaxValue)
                throw new Exception($"PLY vertex payload is too large for the current runtime reader: {totalVertexBytes:N0} bytes");

            var allVertexData = new byte[(int)totalVertexBytes];
            ReadExactly(fs, allVertexData);

            bool littleEndian = header.Format == PlyFormat.BinaryLittleEndian;
            var splats = new RuntimeSplatLoader.SplatData[header.VertexCount];
            for (int vertex = 0; vertex < header.VertexCount; vertex++)
            {
                int rowStart = vertex * header.VertexStride;
                splats[vertex] = DecodeBinarySplat(allVertexData, rowStart, littleEndian, schema, options);
            }

            return splats;
        }

        static RuntimeSplatLoader.SplatData[] ReadAsciiVertices(FileStream fs, PlyHeader header, PlySchema schema, SplatImportOptions options)
        {
            var splats = new RuntimeSplatLoader.SplatData[header.VertexCount];
            using var reader = new StreamReader(fs, Encoding.ASCII, false, 4096, leaveOpen: true);
            for (int vertex = 0; vertex < header.VertexCount; vertex++)
            {
                string line = reader.ReadLine();
                if (line == null)
                    throw new Exception($"PLY ASCII payload ended after {vertex} of {header.VertexCount} vertices.");

                string[] values = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (values.Length < header.Properties.Count)
                    throw new Exception($"PLY ASCII vertex {vertex} has {values.Length} values, expected {header.Properties.Count}.");

                splats[vertex] = DecodeAsciiSplat(values, schema, options);
            }

            return splats;
        }

        static RuntimeSplatLoader.SplatData DecodeBinarySplat(byte[] data, int rowStart, bool littleEndian, PlySchema schema, SplatImportOptions options)
        {
            var splat = new RuntimeSplatLoader.SplatData();

            splat.pos = options.TransformPosition(new float3(
                ReadScalar(data, rowStart, littleEndian, schema.X),
                ReadScalar(data, rowStart, littleEndian, schema.Y),
                ReadScalar(data, rowStart, littleEndian, schema.Z)));

            DecodeAppearance(
                ref splat,
                prop => ReadScalar(data, rowStart, littleEndian, prop),
                schema,
                options);

            return splat;
        }

        static RuntimeSplatLoader.SplatData DecodeAsciiSplat(string[] values, PlySchema schema, SplatImportOptions options)
        {
            var splat = new RuntimeSplatLoader.SplatData();

            splat.pos = options.TransformPosition(new float3(
                ReadScalar(values, schema.X),
                ReadScalar(values, schema.Y),
                ReadScalar(values, schema.Z)));

            DecodeAppearance(
                ref splat,
                prop => ReadScalar(values, prop),
                schema,
                options);

            return splat;
        }

        static void DecodeAppearance(ref RuntimeSplatLoader.SplatData splat, Func<PlyProperty, float> read, PlySchema schema, SplatImportOptions options)
        {
            if (schema.Dc0 != null || schema.Dc1 != null || schema.Dc2 != null)
            {
                float3 rawDc0 = new float3(
                    ReadOrDefault(read, schema.Dc0, 0f),
                    ReadOrDefault(read, schema.Dc1, 0f),
                    ReadOrDefault(read, schema.Dc2, 0f));
                splat.dc0 = GaussianUtils.SH0ToColor(rawDc0);
            }
            else if (schema.Red != null && schema.Green != null && schema.Blue != null)
            {
                splat.dc0 = new float3(
                    DecodeColor(read(schema.Red), schema.Red),
                    DecodeColor(read(schema.Green), schema.Green),
                    DecodeColor(read(schema.Blue), schema.Blue));
            }
            else
            {
                splat.dc0 = new float3(0.5f, 0.5f, 0.5f);
            }

            splat.opacity = DecodeOpacity(read, schema.Opacity);
            splat.scale = DecodeScale(read, schema);
            splat.rot = DecodeRotation(read, schema, options);
            splat.sh = DecodeSh(read, schema);
        }

        static float DecodeColor(float raw, PlyProperty prop)
        {
            if (IsInteger(prop.Type))
            {
                if (prop.Type == PlyScalarType.UInt8 || prop.Type == PlyScalarType.Int8 || raw <= 255f)
                    return math.saturate(raw / 255f);

                return math.saturate(raw / MaxIntegerValue(prop.Type));
            }

            return math.saturate(raw > 1f ? raw / 255f : raw);
        }

        static float DecodeOpacity(Func<PlyProperty, float> read, PlyProperty prop)
        {
            if (prop == null)
                return 1f;

            float raw = read(prop);
            if (IsInteger(prop.Type))
                return math.saturate(raw / MaxIntegerValue(prop.Type));

            string name = prop.Name.ToLowerInvariant();
            if (name == "alpha" || name == "a" || name.Contains("linear") || name.Contains("direct"))
                return math.saturate(raw);

            return GaussianUtils.Sigmoid(raw);
        }

        static float3 DecodeScale(Func<PlyProperty, float> read, PlySchema schema)
        {
            if (schema.Scale0 != null || schema.Scale1 != null || schema.Scale2 != null)
            {
                return GaussianUtils.LinearScale(new float3(
                    ReadOrDefault(read, schema.Scale0, 0f),
                    ReadOrDefault(read, schema.Scale1, 0f),
                    ReadOrDefault(read, schema.Scale2, 0f)));
            }

            if (schema.DirectScaleX != null || schema.DirectScaleY != null || schema.DirectScaleZ != null)
            {
                return math.abs(new float3(
                    ReadOrDefault(read, schema.DirectScaleX, 1f),
                    ReadOrDefault(read, schema.DirectScaleY, 1f),
                    ReadOrDefault(read, schema.DirectScaleZ, 1f)));
            }

            return new float3(1f, 1f, 1f);
        }

        static float4 DecodeRotation(Func<PlyProperty, float> read, PlySchema schema, SplatImportOptions options)
        {
            float4 q;
            if (schema.Rot0 != null || schema.Rot1 != null || schema.Rot2 != null || schema.Rot3 != null)
            {
                q = NormalizeWxyzToXyzw(new float4(
                    ReadOrDefault(read, schema.Rot0, 1f),
                    ReadOrDefault(read, schema.Rot1, 0f),
                    ReadOrDefault(read, schema.Rot2, 0f),
                    ReadOrDefault(read, schema.Rot3, 0f)));
            }
            else if (schema.Qx != null || schema.Qy != null || schema.Qz != null || schema.Qw != null)
            {
                q = NormalizeSafe(new float4(
                    ReadOrDefault(read, schema.Qx, 0f),
                    ReadOrDefault(read, schema.Qy, 0f),
                    ReadOrDefault(read, schema.Qz, 0f),
                    ReadOrDefault(read, schema.Qw, 1f)));
            }
            else
            {
                q = new float4(0f, 0f, 0f, 1f);
            }

            q = options.TransformRotation(q);
            return GaussianUtils.PackSmallest3Rotation(q);
        }

        static float3[] DecodeSh(Func<PlyProperty, float> read, PlySchema schema)
        {
            bool hasSh = false;
            for (int i = 0; i < schema.RestSh.Length; i++)
            {
                if (schema.RestSh[i] != null)
                {
                    hasSh = true;
                    break;
                }
            }

            if (!hasSh)
                return null;

            var sh = new float3[15];
            for (int band = 0; band < 15; band++)
            {
                sh[band] = new float3(
                    ReadOrDefault(read, schema.RestSh[band], 0f),
                    ReadOrDefault(read, schema.RestSh[band + 15], 0f),
                    ReadOrDefault(read, schema.RestSh[band + 30], 0f));
            }

            return sh;
        }

        static float ReadOrDefault(Func<PlyProperty, float> read, PlyProperty prop, float fallback)
        {
            return prop != null ? read(prop) : fallback;
        }

        static float4 NormalizeWxyzToXyzw(float4 wxyz)
        {
            float4 q = NormalizeSafe(wxyz);
            return q.yzwx;
        }

        static float4 NormalizeSafe(float4 q)
        {
            float lenSq = math.lengthsq(q);
            if (lenSq <= 1e-12f)
                return new float4(0f, 0f, 0f, 1f);
            return q * math.rsqrt(lenSq);
        }

        static bool IsInteger(PlyScalarType type)
        {
            return type == PlyScalarType.Int8
                || type == PlyScalarType.UInt8
                || type == PlyScalarType.Int16
                || type == PlyScalarType.UInt16
                || type == PlyScalarType.Int32
                || type == PlyScalarType.UInt32;
        }

        static float MaxIntegerValue(PlyScalarType type)
        {
            return type switch
            {
                PlyScalarType.Int8 => sbyte.MaxValue,
                PlyScalarType.UInt8 => byte.MaxValue,
                PlyScalarType.Int16 => short.MaxValue,
                PlyScalarType.UInt16 => ushort.MaxValue,
                PlyScalarType.Int32 => int.MaxValue,
                PlyScalarType.UInt32 => uint.MaxValue,
                _ => 1f
            };
        }

        static float ReadScalar(string[] values, PlyProperty prop)
        {
            return float.Parse(values[prop.Index], CultureInfo.InvariantCulture);
        }

        static float ReadScalar(byte[] data, int rowStart, bool littleEndian, PlyProperty prop)
        {
            int offset = rowStart + prop.Offset;
            return prop.Type switch
            {
                PlyScalarType.Int8 => unchecked((sbyte)data[offset]),
                PlyScalarType.UInt8 => data[offset],
                PlyScalarType.Int16 => ReadInt16(data, offset, littleEndian),
                PlyScalarType.UInt16 => ReadUInt16(data, offset, littleEndian),
                PlyScalarType.Int32 => ReadInt32(data, offset, littleEndian),
                PlyScalarType.UInt32 => ReadUInt32(data, offset, littleEndian),
                PlyScalarType.Float32 => ReadFloat32(data, offset, littleEndian),
                PlyScalarType.Float64 => (float)ReadFloat64(data, offset, littleEndian),
                _ => throw new ArgumentOutOfRangeException()
            };
        }

        static short ReadInt16(byte[] data, int offset, bool littleEndian)
        {
            ushort value = ReadUInt16(data, offset, littleEndian);
            return unchecked((short)value);
        }

        static ushort ReadUInt16(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian == littleEndian)
                return BitConverter.ToUInt16(data, offset);

            return (ushort)((data[offset] << 8) | data[offset + 1]);
        }

        static int ReadInt32(byte[] data, int offset, bool littleEndian)
        {
            uint value = ReadUInt32(data, offset, littleEndian);
            return unchecked((int)value);
        }

        static uint ReadUInt32(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian == littleEndian)
                return BitConverter.ToUInt32(data, offset);

            return ((uint)data[offset] << 24)
                | ((uint)data[offset + 1] << 16)
                | ((uint)data[offset + 2] << 8)
                | data[offset + 3];
        }

        static ulong ReadUInt64(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian == littleEndian)
                return BitConverter.ToUInt64(data, offset);

            return ((ulong)data[offset] << 56)
                | ((ulong)data[offset + 1] << 48)
                | ((ulong)data[offset + 2] << 40)
                | ((ulong)data[offset + 3] << 32)
                | ((ulong)data[offset + 4] << 24)
                | ((ulong)data[offset + 5] << 16)
                | ((ulong)data[offset + 6] << 8)
                | data[offset + 7];
        }

        static float ReadFloat32(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian == littleEndian)
                return BitConverter.ToSingle(data, offset);

            byte[] tmp = { data[offset + 3], data[offset + 2], data[offset + 1], data[offset] };
            return BitConverter.ToSingle(tmp, 0);
        }

        static double ReadFloat64(byte[] data, int offset, bool littleEndian)
        {
            if (BitConverter.IsLittleEndian == littleEndian)
                return BitConverter.ToDouble(data, offset);

            ulong bits = ReadUInt64(data, offset, littleEndian);
            byte[] tmp = BitConverter.GetBytes(bits);
            return BitConverter.ToDouble(tmp, 0);
        }

        static void ReadExactly(Stream stream, byte[] data)
        {
            int totalRead = 0;
            while (totalRead < data.Length)
            {
                int read = stream.Read(data, totalRead, data.Length - totalRead);
                if (read == 0)
                    throw new EndOfStreamException($"PLY payload truncated: read {totalRead:N0} of {data.Length:N0} bytes.");
                totalRead += read;
            }
        }
    }
}

sealed class SpzSplatInputAdapter : ISplatInputAdapter
{
    public string DisplayName => "Niantic SPZ";
    public bool SupportsExtension(string extension) => extension == ".spz";

    public long EstimateRuntimeBytes(string filePath)
    {
        var fi = new FileInfo(filePath);
        return Math.Max(fi.Exists ? fi.Length / 22 : 1, 1) * 236L;
    }

    public RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        var options = SplatImportOptions.FromFilePath(filePath);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        byte[] raw = ms.ToArray();

        if (raw.Length < 16)
            throw new Exception("SPZ file too small for header");

        uint magic = BitConverter.ToUInt32(raw, 0);
        uint version = BitConverter.ToUInt32(raw, 4);
        uint numPoints = BitConverter.ToUInt32(raw, 8);
        uint shFracFlags = BitConverter.ToUInt32(raw, 12);

        if (magic != 0x5053474E)
            throw new Exception($"Invalid SPZ magic: 0x{magic:X8}");
        if (version < 2 || version > 3)
            throw new Exception($"Unsupported SPZ version: {version}");
        if (numPoints > 10_000_000)
            throw new Exception($"SPZ numPoints too large: {numPoints}");

        int shLevel = (int)(shFracFlags & 0xFF);
        int fractBits = (int)((shFracFlags >> 8) & 0xFF);
        float fractScale = 1.0f / (1 << fractBits);

        int[] shCoeffsTable = { 0, 3, 8, 15 };
        int shCoeffs = (shLevel >= 0 && shLevel <= 3) ? shCoeffsTable[shLevel] : 0;

        int n = (int)numPoints;
        int expectedBytes = 16 + n * (9 + 1 + 3 + 3 + 3 + 3 * shCoeffs);
        if (raw.Length < expectedBytes)
            throw new Exception($"SPZ file truncated: {raw.Length} < {expectedBytes}");

        int off = 16;
        int posOff = off;           off += n * 9;
        int alphaOff = off;         off += n;
        int colorOff = off;         off += n * 3;
        int scaleOff = off;         off += n * 3;
        int rotOff = off;           off += n * 3;
        int shOff = off;

        var splats = new RuntimeSplatLoader.SplatData[n];
        for (int i = 0; i < n; i++)
        {
            ref RuntimeSplatLoader.SplatData splat = ref splats[i];

            int pBase = posOff + i * 9;
            splat.pos = options.TransformPosition(new float3(
                SignExtend24(raw[pBase] | (raw[pBase + 1] << 8) | (raw[pBase + 2] << 16)) * fractScale,
                SignExtend24(raw[pBase + 3] | (raw[pBase + 4] << 8) | (raw[pBase + 5] << 16)) * fractScale,
                SignExtend24(raw[pBase + 6] | (raw[pBase + 7] << 8) | (raw[pBase + 8] << 16)) * fractScale));

            splat.opacity = raw[alphaOff + i] / 255f;

            int cBase = colorOff + i * 3;
            float3 col = new float3(raw[cBase], raw[cBase + 1], raw[cBase + 2]) / 255f - 0.5f;
            col /= 0.15f;
            splat.dc0 = GaussianUtils.SH0ToColor(col);

            int sBase = scaleOff + i * 3;
            float3 logScale = new float3(
                raw[sBase] / 16f - 10f,
                raw[sBase + 1] / 16f - 10f,
                raw[sBase + 2] / 16f - 10f);
            splat.scale = GaussianUtils.LinearScale(logScale);

            int rBase = rotOff + i * 3;
            float3 rxyz = new float3(
                raw[rBase] / 127.5f - 1f,
                raw[rBase + 1] / 127.5f - 1f,
                raw[rBase + 2] / 127.5f - 1f);
            float rw = math.sqrt(math.max(0f, 1f - math.dot(rxyz, rxyz)));
            float4 q = math.normalize(new float4(rxyz, rw));
            q = options.TransformRotation(q);
            splat.rot = GaussianUtils.PackSmallest3Rotation(q);

            if (shCoeffs > 0)
            {
                splat.sh = new float3[15];
                int shBase = shOff + i * 3 * shCoeffs;
                for (int j = 0; j < shCoeffs && j < 15; j++)
                {
                    int b = shBase + j * 3;
                    splat.sh[j] = new float3(
                        (raw[b] - 128f) / 128f,
                        (raw[b + 1] - 128f) / 128f,
                        (raw[b + 2] - 128f) / 128f);
                }
            }
        }

        return splats;
    }

    static int SignExtend24(int value)
    {
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }
}

sealed class SogSplatInputAdapter : ISplatInputAdapter
{
    public string DisplayName => "PlayCanvas SOG";
    public bool SupportsExtension(string extension) => extension == ".sog";

    public long EstimateRuntimeBytes(string filePath)
    {
        var fi = new FileInfo(filePath);
        return Math.Max(fi.Exists ? fi.Length / 25 : 1, 1) * 236L;
    }

    public RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        return PlayCanvasSogReader.ReadFile(filePath);
    }
}

sealed class SpxSplatInputAdapter : ISplatInputAdapter
{
    public string DisplayName => "Reall3D SPX";
    public bool SupportsExtension(string extension) => extension == ".spx";

    public long EstimateRuntimeBytes(string filePath)
    {
        var fi = new FileInfo(filePath);
        return Math.Max(fi.Exists ? fi.Length / 30 : 1, 1) * 236L;
    }

    public RuntimeSplatLoader.SplatData[] ReadFile(string filePath)
    {
        return SpxReader.ReadFile(filePath);
    }
}
