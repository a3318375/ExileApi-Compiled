using System.Buffers.Binary;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

// The core ships as ReadyToRun with incomplete fallback IL. Keep native code,
// method RVAs, and every referenced string token in the original version bubble.
internal sealed class CoreImage : IDisposable
{
    readonly byte[] image;
    readonly PEReader pe;
    readonly MetadataReader md;
    readonly int root;
    readonly int headersEnd;
    readonly Dictionary<string, (int Header, byte[] Bytes)> streams = new();
    readonly MemoryStream blobs = new();
    readonly List<(int Parent, int Constructor, int Blob)> rows = new();
    readonly int parentWidth;
    readonly int ctorWidth;
    readonly int blobWidth;
    readonly int attributeTable;

    static int U16(byte[] buf, int pos) => BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos));
    static int I32(byte[] buf, int pos) => BinaryPrimitives.ReadInt32LittleEndian(buf.AsSpan(pos));
    static void Put32(byte[] buf, int pos, int value) => BinaryPrimitives.WriteInt32LittleEndian(buf.AsSpan(pos), value);
    static int Align(int value, int multiple) => (value + multiple - 1) / multiple * multiple;
    static int Index(byte[] buf, int pos, int size) => size == 2 ? U16(buf, pos) : I32(buf, pos);
    static void WriteIndex(BinaryWriter writer, int value, int width)
    {
        if (width == 2) writer.Write(checked((ushort)value));
        else writer.Write(value);
    }

    public CoreImage(string file)
    {
        image = File.ReadAllBytes(file);
        pe = new PEReader(new MemoryStream(image, writable: false));
        md = pe.GetMetadataReader();
        root = pe.PEHeaders.MetadataStartOffset;
        if (pe.PEHeaders.CorHeader!.ManagedNativeHeaderDirectory.Size == 0)
            throw new InvalidDataException("Expected the upstream ReadyToRun core");
        int p = root + 16 + I32(image, root + 12);
        int count = U16(image, p + 2);
        p += 4;
        for (int i = 0; i < count; i++)
        {
            int header = p;
            int offset = I32(image, p), size = I32(image, p + 4);
            p += 8;
            int end = Array.IndexOf(image, (byte)0, p);
            string name = Encoding.ASCII.GetString(image, p, end - p);
            streams.Add(name, (header - root, image.AsSpan(root + offset, size).ToArray()));
            p = root + Align(end + 1 - root, 4);
        }
        headersEnd = p - root;
        blobs.Write(streams["#Blob"].Bytes);
        var tables = streams["#~"].Bytes;
        blobWidth = (tables[6] & 4) == 0 ? 2 : 4;
        ctorWidth = Math.Max(md.MethodDefinitions.Count, md.MemberReferences.Count) < (1 << 13) ? 2 : 4;
        int stride = md.GetTableRowSize(TableIndex.CustomAttribute);
        parentWidth = stride - ctorWidth - blobWidth;
        attributeTable = md.GetTableMetadataOffset(TableIndex.CustomAttribute) - I32(image, root + streams["#~"].Header);
        for (int i = 0; i < md.CustomAttributes.Count; i++)
        {
            int row = attributeTable + i * stride;
            rows.Add((Index(tables, row, parentWidth), Index(tables, row + parentWidth, ctorWidth),
                Index(tables, row + parentWidth + ctorWidth, blobWidth)));
        }
    }

    public void SetLabel(int propertyRid, int defaultCtorRid, string name, bool hasTooltip,
        IReadOnlyDictionary<string, string> tooltips)
    {
        var property = md.GetPropertyDefinition(MetadataTokens.PropertyDefinitionHandle(propertyRid));
        // HasCustomAttribute tag 9 = Property, CustomAttributeType tag 2 = MethodDef.
        int parent = (propertyRid << 5) | 9, ctor = (defaultCtorRid << 3) | 2;
        int rowIndex = -1;
        var blob = new BlobBuilder();
        blob.WriteUInt16(1);
        blob.WriteSerializedString(name);
        foreach (var handle in property.GetCustomAttributes())
        {
            var a = md.GetCustomAttribute(handle);
            if (a.Constructor.Kind != HandleKind.MethodDefinition) continue;
            var method = md.GetMethodDefinition((MethodDefinitionHandle)a.Constructor);
            if (md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name) != "MenuAttribute") continue;
            rowIndex = MetadataTokens.GetRowNumber(handle) - 1;
            ctor = rows[rowIndex].Constructor;
            var reader = md.GetBlobReader(a.Value);
            if (reader.ReadUInt16() != 1) throw new InvalidDataException("Invalid attribute prolog");
            reader.ReadSerializedString();
            if (hasTooltip)
            {
                var tip = reader.ReadSerializedString();
                blob.WriteSerializedString(tip is null ? null : tooltips[tip]);
            }
            // Preserve all numeric hierarchy arguments and named fields/properties.
            blob.WriteBytes(reader.ReadBytes(reader.RemainingBytes));
            break;
        }
        if (rowIndex < 0) blob.WriteUInt16(0);
        int offset = checked((int)blobs.Position);
        var length = new BlobBuilder();
        length.WriteCompressedInteger(blob.Count);
        length.WriteContentTo(blobs);
        blob.WriteContentTo(blobs);
        var row = (parent, ctor, offset);
        if (rowIndex < 0) rows.Add(row);
        else rows[rowIndex] = row;
    }

    public void AddFavoritesConverter(int propertyRid)
    {
        // Reuse the existing Newtonsoft.Json.JsonConverterAttribute(Type)
        // member reference without changing any of the upstream metadata tokens.
        var exemplar = md.CustomAttributes.First(h =>
        {
            var attr = md.GetCustomAttribute(h);
            if (attr.Constructor.Kind != HandleKind.MemberReference) return false;
            var member = md.GetMemberReference((MemberReferenceHandle)attr.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) return false;
            var type = md.GetTypeReference((TypeReferenceHandle)member.Parent);
            return md.GetString(type.Name) == "JsonConverterAttribute" && md.GetString(type.Namespace) == "Newtonsoft.Json"
                && md.GetBlobBytes(member.Signature)[1] == 1;
        });
        var data = new BlobBuilder();
        data.WriteUInt16(1);
        data.WriteSerializedString("MenuZhCN.FavoritesConverter, MenuZhCN");
        data.WriteUInt16(0);
        int offset = (int)blobs.Position;
        var length = new BlobBuilder();
        length.WriteCompressedInteger(data.Count);
        length.WriteContentTo(blobs);
        data.WriteContentTo(blobs);
        rows.Add(((propertyRid << 5) | 9, rows[MetadataTokens.GetRowNumber(exemplar) - 1].Constructor, offset));
    }

    public void TranslateLiterals(Dictionary<string, string> labels)
    {
        var us = streams["#US"].Bytes;
        HashSet<string> missing = new(labels.Keys);
        for (var h = MetadataTokens.UserStringHandle(1); !h.IsNil; h = md.GetNextHandle(h))
        {
            var text = md.GetUserString(h);
            if (!labels.TryGetValue(text, out var target)) continue;
            var translated = new BlobBuilder();
            translated.WriteUserString(target);
            int pos = MetadataTokens.GetHeapOffset(h);
            var original = new BlobBuilder();
            original.WriteUserString(text);
            if (translated.Count > original.Count)
                throw new InvalidDataException($"Core string exceeds its token slot: '{text}' -> '{target}'");
            // A shorter entry leaves an unused slot. Fill it with a valid heap entry
            // so metadata enumerators can still walk on to the next original token.
            var padding = original.Count - translated.Count;
            var span = us.AsSpan(pos, original.Count);
            span.Clear();
            translated.ToArray().CopyTo(span);
            if (padding > 0)
            {
                int at = translated.Count;
                while (padding > 1)
                {
                    int size = Math.Min(padding, 128);
                    if ((size & 1) != 0) size--;
                    span[at] = (byte)(size - 1);
                    at += size;
                    padding -= size;
                }
            }
            missing.Remove(text);
        }
        if (missing.Count != 0) throw new InvalidDataException("Missing core labels: " + string.Join(", ", missing));
    }

    public void Write(string output)
    {
        if (blobWidth == 2 && blobs.Length >= 65536) throw new InvalidDataException("Blob index width changed");
        var tables = streams["#~"].Bytes;
        int stride = md.GetTableRowSize(TableIndex.CustomAttribute);
        using var expanded = new MemoryStream();
        using var writer = new BinaryWriter(expanded);
        writer.Write(tables.AsSpan(0, attributeTable));
        foreach (var row in rows.OrderBy(r => r.Parent))
        {
            WriteIndex(writer, row.Parent, parentWidth);
            WriteIndex(writer, row.Constructor, ctorWidth);
            WriteIndex(writer, row.Blob, blobWidth);
        }
        writer.Write(tables.AsSpan(attributeTable + md.CustomAttributes.Count * stride));
        var newTables = expanded.ToArray();
        ulong valid = BinaryPrimitives.ReadUInt64LittleEndian(tables.AsSpan(8));
        int countOffset = 24 + 4 * System.Numerics.BitOperations.PopCount(valid & ((1UL << (int)TableIndex.CustomAttribute) - 1));
        Put32(newTables, countOffset, rows.Count);
        streams["#~"] = (streams["#~"].Header, newTables);
        streams["#Blob"] = (streams["#Blob"].Header, blobs.ToArray());
        using var metadata = new MemoryStream();
        metadata.Write(image.AsSpan(root, headersEnd));
        foreach (var (header, bytes) in streams.Values)
        {
            int offset = (int)metadata.Position;
            metadata.Write(bytes);
            while (metadata.Position % 4 != 0) metadata.WriteByte(0);
            Put32(metadata.GetBuffer(), header, offset);
            Put32(metadata.GetBuffer(), header + 4, (int)metadata.Position - offset);
        }
        AppendMetadata(output, metadata.ToArray());
    }

    void AppendMetadata(string output, byte[] metadata)
    {
        int coff = I32(image, 0x3c) + 4, optional = coff + 20;
        var headers = pe.PEHeaders;
        var opt = headers.PEHeader!;
        var sections = headers.SectionHeaders;
        var last = sections[^1];
        int entry = optional + U16(image, coff + 16) + 40 * sections.Length;
        if (entry + 40 > sections.Min(s => s.PointerToRawData) || opt.CertificateTableDirectory.Size != 0)
            throw new InvalidDataException("Unexpected PE headers or signature");
        int rva = Align(last.VirtualAddress + Math.Max(last.VirtualSize, last.SizeOfRawData), opt.SectionAlignment);
        int raw = Align(image.Length, opt.FileAlignment), rawSize = Align(metadata.Length, opt.FileAlignment);
        byte[] result = new byte[raw + rawSize];
        image.CopyTo(result, 0);
        result.AsSpan(entry, 40).Clear();
        ".zhcn"u8.CopyTo(result.AsSpan(entry));
        Put32(result, entry + 8, metadata.Length);
        Put32(result, entry + 12, rva);
        Put32(result, entry + 16, rawSize);
        Put32(result, entry + 20, raw);
        Put32(result, entry + 36, 0x40000040); // initialized, read-only data
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(coff + 2), checked((ushort)(sections.Length + 1)));
        Put32(result, optional + 8, opt.SizeOfInitializedData + rawSize);
        Put32(result, optional + 56, Align(rva + metadata.Length, opt.SectionAlignment));
        Put32(result, optional + 64, 0);
        Put32(result, headers.CorHeaderStartOffset + 8, rva);
        Put32(result, headers.CorHeaderStartOffset + 12, metadata.Length);
        metadata.CopyTo(result, raw);
        File.WriteAllBytes(output, result);
    }

    public void Dispose()
    {
        blobs.Dispose();
        pe.Dispose();
    }
}
