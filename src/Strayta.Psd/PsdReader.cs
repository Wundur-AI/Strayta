using Strayta.Core;

namespace Strayta.Psd;

/// <summary>Parses a PSD or PSB file into a <see cref="PsdFile"/>.</summary>
internal sealed class PsdReader(Stream stream, PsdReadOptions options)
{
    // Tagged block keys whose length field is 8 bytes in PSB files.
    private static readonly HashSet<string> WideLengthKeys =
        ["LMsk", "Lr16", "Lr32", "Layr", "Mt16", "Mt32", "Mtrn", "Alph", "FMsk", "lnk2", "FEid", "FXid", "PxSD"];

    private static readonly HashSet<string> LayerInfoKeys = ["Layr", "Lr16", "Lr32"];

    private readonly BigEndianReader _r = new(stream);
    private PsdHeader _header = null!;

    private bool Psb => _header.IsPsb;

    public PsdFile Read()
    {
        _header = ReadHeader();
        var colorModeData = _r.ReadBytes(_r.ReadUInt32());
        var resources = ReadImageResources();

        var layers = new List<PsdLayerRecord>();
        bool compositeHasTransparency = false;
        byte[] globalMask = [];
        List<TaggedBlock> globalBlocks = [];

        long lmiLength = _r.ReadLength(Psb);
        long lmiEnd = _r.Position + lmiLength;
        if (lmiLength > 0)
        {
            long layerInfoLength = _r.ReadLength(Psb);
            long layerInfoEnd = _r.Position + layerInfoLength;
            if (layerInfoLength > 0)
                compositeHasTransparency = ReadLayerInfo(layers, layerInfoEnd);
            _r.Position = layerInfoEnd;

            if (_r.Position + 4 <= lmiEnd)
            {
                long globalMaskLength = _r.ReadUInt32();
                globalMask = _r.ReadBytes(Math.Min(globalMaskLength, lmiEnd - _r.Position));
            }

            // 16- and 32-bit documents keep their layers in an Lr16/Lr32 block here instead.
            globalBlocks = ReadTaggedBlocks(lmiEnd, (key, dataEnd) =>
            {
                if (!LayerInfoKeys.Contains(key) || layers.Count > 0) return false;
                compositeHasTransparency = ReadLayerInfoBlock(layers, dataEnd);
                return true;
            });
        }
        _r.Position = lmiEnd;

        var composite = options.SkipComposite || _r.Position >= _r.Length
            ? []
            : ReadComposite();

        return new PsdFile
        {
            Header = _header,
            ColorModeData = colorModeData,
            Resources = resources,
            Layers = layers,
            CompositeHasTransparency = compositeHasTransparency,
            GlobalLayerMaskInfo = globalMask,
            GlobalBlocks = globalBlocks,
            CompositeChannels = composite,
            HasRealMergedData = ReadHasRealMergedData(resources),
        };
    }

    private PsdHeader ReadHeader()
    {
        if (_r.ReadSignature() != "8BPS") throw new PsdFormatException("Not a PSD file: missing 8BPS signature", 0);
        int version = _r.ReadUInt16();
        if (version is not (1 or 2)) throw new PsdFormatException($"Unsupported version {version}", 4);
        _r.Skip(6);
        int channels = _r.ReadUInt16();
        int height = _r.ReadInt32();
        int width = _r.ReadInt32();
        int depth = _r.ReadUInt16();
        var mode = (ColorMode)_r.ReadUInt16();

        int maxSize = version == 2 ? 300_000 : 30_000;
        if (channels is < 1 or > 56) throw new PsdFormatException($"Invalid channel count {channels}", 12);
        if (height < 1 || height > maxSize || width < 1 || width > maxSize)
            throw new PsdFormatException($"Invalid dimensions {width}x{height}", 14);
        if (depth is not (1 or 8 or 16 or 32)) throw new PsdFormatException($"Invalid bit depth {depth}", 22);
        if (!Enum.IsDefined(mode)) throw new PsdFormatException($"Invalid color mode {(int)mode}", 24);

        return new PsdHeader(version, channels, width, height, depth, mode);
    }

    private List<ImageResource> ReadImageResources()
    {
        long length = _r.ReadUInt32();
        long end = _r.Position + length;
        var list = new List<ImageResource>();
        while (_r.Position + 12 <= end)
        {
            long start = _r.Position;
            string sig = _r.ReadSignature();
            if (sig is not ("8BIM" or "MeSa" or "AgHg" or "PHUT" or "DCSR"))
                throw new PsdFormatException($"Bad image resource signature '{sig}'", start);
            int id = _r.ReadUInt16();
            string name = _r.ReadPascalString(2);
            long size = _r.ReadUInt32();
            var data = _r.ReadBytes(Math.Min(size, end - _r.Position));
            if ((size & 1) != 0 && _r.Position < end) _r.Skip(1);
            list.Add(new ImageResource(sig, id, name, data));
        }
        _r.Position = end;
        return list;
    }

    private static bool? ReadHasRealMergedData(List<ImageResource> resources)
    {
        var info = resources.FirstOrDefault(r => r.Id == 1057);
        return info?.Data is { Length: >= 5 } d ? d[4] != 0 : null;
    }

    /// <summary>Lr16/Lr32 blocks may or may not repeat the layer info length; detect which.</summary>
    private bool ReadLayerInfoBlock(List<PsdLayerRecord> layers, long end)
    {
        long start = _r.Position;
        long remaining = end - start;
        int lengthSize = Psb ? 8 : 4;
        if (remaining >= lengthSize)
        {
            long maybeLength = _r.ReadLength(Psb);
            long body = remaining - lengthSize;
            if (maybeLength > 0 && maybeLength <= body && body - maybeLength < 4)
                return ReadLayerInfo(layers, end);
            _r.Position = start;
        }
        return ReadLayerInfo(layers, end);
    }

    /// <summary>Reads layer records and their channel data. Returns true when the layer count was negative.</summary>
    private bool ReadLayerInfo(List<PsdLayerRecord> layers, long end)
    {
        int count = _r.ReadInt16();
        bool negative = count < 0;
        count = Math.Abs(count);

        for (int i = 0; i < count; i++)
            layers.Add(ReadLayerRecord());

        ReadAllLayerChannels(layers, end);
        return negative;
    }

    private PsdLayerRecord ReadLayerRecord()
    {
        long start = _r.Position;
        int top = _r.ReadInt32(), left = _r.ReadInt32(), bottom = _r.ReadInt32(), right = _r.ReadInt32();
        int channelCount = _r.ReadUInt16();
        if (channelCount > 56) throw new PsdFormatException($"Layer has {channelCount} channels", start);

        var channels = new List<PsdChannelInfo>(channelCount);
        for (int c = 0; c < channelCount; c++)
            channels.Add(new PsdChannelInfo(_r.ReadInt16(), _r.ReadLength(Psb)));

        string sig = _r.ReadSignature();
        if (sig != "8BIM") throw new PsdFormatException($"Bad blend mode signature '{sig}'", _r.Position - 4);
        string blendKey = _r.ReadSignature();
        byte opacity = _r.ReadByte();
        byte clipping = _r.ReadByte();
        byte flags = _r.ReadByte();
        _r.Skip(1);

        long extraLength = _r.ReadUInt32();
        long extraEnd = _r.Position + extraLength;

        var mask = ReadLayerMaskData();

        long rangesLength = _r.ReadUInt32();
        var ranges = _r.ReadBytes(rangesLength);

        string name = _r.ReadPascalString(4);
        var blocks = ReadTaggedBlocks(extraEnd, null);
        _r.Position = extraEnd;

        return new PsdLayerRecord
        {
            Rect = new PixelRect(left, top, right, bottom),
            Channels = channels,
            BlendModeKey = blendKey,
            Opacity = opacity,
            Clipped = clipping != 0,
            Flags = flags,
            Mask = mask,
            BlendingRanges = ranges,
            PascalName = name,
            Blocks = blocks,
        };
    }

    private PsdLayerMaskData? ReadLayerMaskData()
    {
        long size = _r.ReadUInt32();
        long end = _r.Position + size;
        if (size < 18)
        {
            _r.Position = end;
            return null;
        }

        var rect = ReadRect();
        byte defaultColor = _r.ReadByte();
        byte flags = _r.ReadByte();

        PixelRect? realRect = null;
        byte? realFlags = null, realDefault = null;
        bool hasParameters = (flags & 0x10) != 0;
        if (!hasParameters && size >= 36)
        {
            realFlags = _r.ReadByte();
            realDefault = _r.ReadByte();
            realRect = ReadRect();
        }

        _r.Position = end;
        return new PsdLayerMaskData
        {
            Rect = rect,
            DefaultColor = defaultColor,
            Flags = flags,
            RealRect = realRect,
            RealFlags = realFlags,
            RealDefaultColor = realDefault,
        };
    }

    private PixelRect ReadRect()
    {
        int top = _r.ReadInt32(), left = _r.ReadInt32(), bottom = _r.ReadInt32(), right = _r.ReadInt32();
        return new PixelRect(left, top, right, bottom);
    }

    /// <summary>
    /// Reads tagged blocks up to <paramref name="end"/>. <paramref name="handler"/> may consume a block itself
    /// (returning true) given its key and data end; otherwise the raw bytes are kept.
    /// Writers disagree on padding between blocks, so the reader resyncs on the next signature.
    /// </summary>
    private List<TaggedBlock> ReadTaggedBlocks(long end, Func<string, long, bool>? handler)
    {
        var blocks = new List<TaggedBlock>();
        while (true)
        {
            int skipped = 0;
            while (skipped < 4 && _r.Position + 12 <= end && _r.PeekSignature() is not ("8BIM" or "8B64"))
            {
                _r.Skip(1);
                skipped++;
            }
            if (_r.Position + 12 > end || _r.PeekSignature() is not ("8BIM" or "8B64")) break;

            string sig = _r.ReadSignature();
            string key = _r.ReadSignature();
            long length = _r.ReadLength(Psb && WideLengthKeys.Contains(key));
            long dataStart = _r.Position;
            long dataEnd = dataStart + length;
            if (dataEnd > end)
                throw new PsdFormatException($"Tagged block '{key}' length {length} runs past its section", dataStart);

            byte[]? data = null;
            if (handler is null || !handler(key, dataEnd))
            {
                if (length <= options.MaxRawBlockBytes)
                {
                    _r.Position = dataStart;
                    data = _r.ReadBytes(length);
                }
            }

            blocks.Add(new TaggedBlock(sig, key, dataStart, length, data));
            _r.Position = dataEnd;
        }
        return blocks;
    }

    /// <summary>Compressed channel bytes read from the file, waiting to be decoded.</summary>
    private readonly record struct ChannelJob(PsdLayerRecord Layer, short Id, PsdCompression Compression, byte[] Data, PixelRect Rect);

    /// <summary>
    /// Reads every layer's compressed channel bytes sequentially (the file must be read in order),
    /// then decompresses them in parallel.
    /// </summary>
    private void ReadAllLayerChannels(List<PsdLayerRecord> layers, long sectionEnd)
    {
        var jobs = new List<ChannelJob>();
        foreach (var layer in layers)
        {
            foreach (var channel in layer.Channels)
            {
                long start = _r.Position;
                long next = start + channel.Length;
                if (next > sectionEnd)
                    throw new PsdFormatException($"Channel {channel.Id} data runs past the layer info section", start);

                if (channel.Length >= 2 && !options.SkipLayerPixels)
                {
                    var rect = channel.Id switch
                    {
                        PsdChannelId.UserMask => layer.Mask?.Rect ?? PixelRect.Empty,
                        PsdChannelId.RealUserMask => layer.Mask?.RealRect ?? PixelRect.Empty,
                        _ => layer.Rect,
                    };
                    var compression = (PsdCompression)_r.ReadUInt16();
                    if (!rect.IsEmpty)
                        jobs.Add(new ChannelJob(layer, channel.Id, compression, _r.ReadBytes(channel.Length - 2), rect));
                }

                _r.Position = next;
            }
        }

        var planes = new Plane[jobs.Count];
        int depth = _header.BitDepth;
        bool psb = Psb;
        Parallel.For(0, jobs.Count, i =>
        {
            var j = jobs[i];
            planes[i] = ChannelDecoder.Decode(j.Data, j.Compression, j.Rect.Width, j.Rect.Height, depth, psb);
        });
        for (int i = 0; i < jobs.Count; i++)
            jobs[i].Layer.ChannelData[jobs[i].Id] = planes[i];
    }

    private List<Plane> ReadComposite()
    {
        var compression = (PsdCompression)_r.ReadUInt16();
        int w = _header.Width, h = _header.Height, depth = _header.BitDepth, channels = _header.Channels;
        int rowBytes = ChannelDecoder.RowBytes(w, depth);
        long planeBytes = (long)rowBytes * h;
        var planes = new List<Plane>(channels);

        switch (compression)
        {
            case PsdCompression.Raw:
                for (int c = 0; c < channels; c++)
                    planes.Add(ChannelDecoder.ToPlane(_r.ReadBytes(planeBytes), w, h, depth));
                break;

            case PsdCompression.Rle:
            {
                // Row byte counts for every row of every channel come first, then the packed rows.
                var counts = new int[channels * h];
                for (int i = 0; i < counts.Length; i++)
                    counts[i] = Psb ? (int)_r.ReadUInt32() : _r.ReadUInt16();
                var packedChannels = new byte[channels][];
                for (int c = 0; c < channels; c++)
                {
                    long packedLength = 0;
                    foreach (int n in counts.AsSpan(c * h, h)) packedLength += n;
                    packedChannels[c] = _r.ReadBytes(packedLength);
                }
                var decoded = new Plane[channels];
                Parallel.For(0, channels, c =>
                {
                    var raw = new byte[planeBytes];
                    ChannelDecoder.DecodeRleRows(packedChannels[c], counts.AsSpan(c * h, h), raw, rowBytes);
                    decoded[c] = ChannelDecoder.ToPlane(raw, w, h, depth);
                });
                planes.AddRange(decoded);
                break;
            }

            case PsdCompression.Zip:
            case PsdCompression.ZipWithPrediction:
            {
                var all = new byte[checked(planeBytes * channels)];
                ChannelDecoder.Inflate(_r.ReadBytes(_r.Length - _r.Position), all);
                for (int c = 0; c < channels; c++)
                {
                    var raw = all.AsSpan((int)(c * planeBytes), (int)planeBytes).ToArray();
                    if (compression == PsdCompression.ZipWithPrediction)
                        ChannelDecoder.Unpredict(raw, w, h, depth);
                    planes.Add(ChannelDecoder.ToPlane(raw, w, h, depth));
                }
                break;
            }

            default:
                throw new PsdFormatException($"Unknown composite compression {(int)compression}", _r.Position - 2);
        }

        return planes;
    }
}
