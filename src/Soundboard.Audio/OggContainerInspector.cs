using System.Buffers.Binary;
using System.Text;

namespace Soundboard.Audio;

internal sealed record OggStreamInfo(
    AudioCodecType Codec,
    int Channels,
    int SampleRate,
    long FinalGranulePosition,
    int OpusPreSkip);

internal static class OggContainerInspector
{
    private const int MaximumIdentificationPacketBytes = 64 * 1024;
    private const int MaximumOggPacketBytes = 16 * 1024 * 1024;

    private static readonly uint[] CrcLookup = CreateCrcLookup();

    public static OggStreamInfo Inspect(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek)
        {
            throw new InvalidDataException(
                "The Ogg file must be readable and seekable.");
        }

        stream.Position = 0;
        if (stream.Length == 0)
        {
            throw new InvalidDataException("The audio file is empty.");
        }

        uint? streamSerial = null;
        uint? expectedSequence = null;
        using var identificationPacket = new MemoryStream();
        var identificationComplete = false;
        var sawBeginning = false;
        var sawEnd = false;
        long finalGranule = -1;
        var pageCount = 0;
        var currentPacketBytes = 0;

        while (stream.Position < stream.Length)
        {
            if (sawEnd)
            {
                throw new NotSupportedException(
                    "Ogg files containing chained or trailing streams are "
                    + "not supported.");
            }

            var header = ReadExactly(stream, 27, "Ogg page header");
            if (!header.AsSpan(0, 4).SequenceEqual("OggS"u8))
            {
                throw new InvalidDataException(
                    pageCount == 0
                        ? "The file is not a valid Ogg container."
                        : "The Ogg container has a corrupt page header.");
            }

            if (header[4] != 0)
            {
                throw new InvalidDataException(
                    $"Ogg bitstream version {header[4]} is not supported.");
            }

            var headerType = header[5];
            if ((headerType & ~0x07) != 0)
            {
                throw new InvalidDataException(
                    "The Ogg page contains invalid header flags.");
            }

            var granule = BinaryPrimitives.ReadInt64LittleEndian(
                header.AsSpan(6, 8));
            var serial = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(14, 4));
            var sequence = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(18, 4));
            var expectedCrc = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(22, 4));
            var segmentCount = header[26];
            var lacing = ReadExactly(
                stream,
                segmentCount,
                "Ogg segment table");
            var bodyLength = 0;
            foreach (var segmentLength in lacing)
            {
                bodyLength = checked(bodyLength + segmentLength);
            }

            var body = ReadExactly(stream, bodyLength, "Ogg page data");
            ValidateCrc(header, lacing, body, expectedCrc);

            if (streamSerial is null)
            {
                streamSerial = serial;
                expectedSequence = sequence;
                sawBeginning = (headerType & 0x02) != 0;
                if (!sawBeginning)
                {
                    throw new InvalidDataException(
                        "The first Ogg page is missing its beginning marker.");
                }

                if (sequence != 0)
                {
                    throw new InvalidDataException(
                        "The first Ogg page has an invalid sequence number.");
                }
            }
            else if (streamSerial.Value != serial)
            {
                throw new NotSupportedException(
                    "Ogg files containing multiple streams or video are "
                    + "not supported.");
            }
            else if ((headerType & 0x02) != 0)
            {
                throw new InvalidDataException(
                    "The Ogg stream has more than one beginning page.");
            }

            if (sequence != expectedSequence)
            {
                throw new InvalidDataException(
                    "The Ogg page sequence is corrupt or incomplete.");
            }

            expectedSequence = unchecked(sequence + 1);
            pageCount++;
            ValidatePacketSizes(
                headerType,
                lacing,
                ref currentPacketBytes);

            if (!identificationComplete)
            {
                AppendIdentificationPacket(
                    identificationPacket,
                    lacing,
                    body,
                    ref identificationComplete);
            }

            if ((headerType & 0x04) != 0)
            {
                sawEnd = true;
                finalGranule = granule;
            }
        }

        if (pageCount == 0 || !identificationComplete)
        {
            throw new InvalidDataException(
                "The Ogg container has no complete identification packet.");
        }

        if (currentPacketBytes != 0)
        {
            throw new InvalidDataException(
                "The final Ogg packet is truncated.");
        }

        if (!sawEnd || finalGranule < 0)
        {
            throw new InvalidDataException(
                "The Ogg stream is incomplete or has no valid end marker.");
        }

        return ParseIdentificationPacket(
            identificationPacket.ToArray(),
            finalGranule);
    }

    private static void ValidatePacketSizes(
        byte headerType,
        byte[] lacing,
        ref int currentPacketBytes)
    {
        var isContinuation = (headerType & 0x01) != 0;
        if ((currentPacketBytes > 0) != isContinuation)
        {
            throw new InvalidDataException(
                "The Ogg packet continuation markers are inconsistent.");
        }

        foreach (var segmentLength in lacing)
        {
            currentPacketBytes = checked(
                currentPacketBytes + segmentLength);
            if (currentPacketBytes > MaximumOggPacketBytes)
            {
                throw new InvalidDataException(
                    "An Ogg packet is unreasonably large.");
            }

            if (segmentLength < byte.MaxValue)
            {
                currentPacketBytes = 0;
            }
        }
    }

    private static void AppendIdentificationPacket(
        MemoryStream packet,
        byte[] lacing,
        byte[] body,
        ref bool complete)
    {
        var bodyOffset = 0;
        foreach (var segmentLength in lacing)
        {
            if (packet.Length + segmentLength
                > MaximumIdentificationPacketBytes)
            {
                throw new InvalidDataException(
                    "The Ogg identification packet is unreasonably large.");
            }

            packet.Write(body, bodyOffset, segmentLength);
            bodyOffset += segmentLength;
            if (segmentLength < byte.MaxValue)
            {
                complete = true;
                break;
            }
        }
    }

    private static OggStreamInfo ParseIdentificationPacket(
        byte[] packet,
        long finalGranule)
    {
        if (packet.AsSpan().StartsWith("OpusHead"u8))
        {
            if (packet.Length < 19)
            {
                throw new InvalidDataException(
                    "The Ogg Opus identification header is truncated.");
            }

            if (packet[8] > 15)
            {
                throw new NotSupportedException(
                    $"Ogg Opus version {packet[8]} is not supported.");
            }

            var channels = packet[9];
            ValidateChannels(channels, "Opus");
            var preSkip = BinaryPrimitives.ReadUInt16LittleEndian(
                packet.AsSpan(10, 2));
            var channelMappingFamily = packet[18];
            if (channelMappingFamily != 0)
            {
                throw new NotSupportedException(
                    "This Ogg Opus channel mapping is not supported. "
                    + "Only normal mono and stereo files are supported.");
            }

            if (finalGranule <= preSkip)
            {
                throw new InvalidDataException(
                    "The Ogg Opus stream contains no playable audio samples.");
            }

            return new OggStreamInfo(
                AudioCodecType.Opus,
                channels,
                48000,
                finalGranule,
                preSkip);
        }

        if (packet.Length >= 7
            && packet[0] == 0x01
            && packet.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            if (packet.Length < 30)
            {
                throw new InvalidDataException(
                    "The Ogg Vorbis identification header is truncated.");
            }

            var version = BinaryPrimitives.ReadUInt32LittleEndian(
                packet.AsSpan(7, 4));
            if (version != 0)
            {
                throw new NotSupportedException(
                    $"Ogg Vorbis version {version} is not supported.");
            }

            var channels = packet[11];
            ValidateChannels(channels, "Vorbis");
            var sampleRate = BinaryPrimitives.ReadInt32LittleEndian(
                packet.AsSpan(12, 4));
            if (sampleRate <= 0 || sampleRate > 384000)
            {
                throw new InvalidDataException(
                    "The Ogg Vorbis sample rate is invalid.");
            }

            if ((packet[29] & 0x01) == 0)
            {
                throw new InvalidDataException(
                    "The Ogg Vorbis identification header is malformed.");
            }

            if (finalGranule <= 0)
            {
                throw new InvalidDataException(
                    "The Ogg Vorbis stream contains no playable audio "
                    + "samples.");
            }

            return new OggStreamInfo(
                AudioCodecType.Vorbis,
                channels,
                sampleRate,
                finalGranule,
                OpusPreSkip: 0);
        }

        var signature = packet.Length == 0
            ? "empty"
            : Encoding.ASCII.GetString(packet, 0, Math.Min(8, packet.Length));
        throw new NotSupportedException(
            $"Unsupported Ogg codec (identification signature: "
            + $"\"{Sanitize(signature)}\"). Only Ogg Opus and Ogg Vorbis "
            + "audio are supported.");
    }

    private static void ValidateChannels(int channels, string codec)
    {
        if (channels is < 1 or > 2)
        {
            throw new NotSupportedException(
                $"{codec} channel count {channels} is not supported. "
                + "Only mono and stereo audio are supported.");
        }
    }

    private static byte[] ReadExactly(
        Stream stream,
        int count,
        string description)
    {
        var buffer = new byte[count];
        var offset = 0;
        while (offset < count)
        {
            var read = stream.Read(buffer, offset, count - offset);
            if (read == 0)
            {
                throw new InvalidDataException(
                    $"The {description} is truncated.");
            }

            offset += read;
        }

        return buffer;
    }

    private static void ValidateCrc(
        byte[] header,
        byte[] lacing,
        byte[] body,
        uint expectedCrc)
    {
        header[22] = 0;
        header[23] = 0;
        header[24] = 0;
        header[25] = 0;
        var actualCrc = UpdateCrc(0, header);
        actualCrc = UpdateCrc(actualCrc, lacing);
        actualCrc = UpdateCrc(actualCrc, body);
        if (actualCrc != expectedCrc)
        {
            throw new InvalidDataException(
                "The Ogg container checksum is invalid.");
        }
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            var index = (byte)((crc >> 24) ^ value);
            crc = (crc << 8) ^ CrcLookup[index];
        }

        return crc;
    }

    private static uint[] CreateCrcLookup()
    {
        var result = new uint[256];
        for (var index = 0; index < result.Length; index++)
        {
            var value = (uint)index << 24;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 0x80000000) != 0
                    ? (value << 1) ^ 0x04C11DB7
                    : value << 1;
            }

            result[index] = value;
        }

        return result;
    }

    private static string Sanitize(string value)
    {
        return string.Concat(
            value.Select(
                character => char.IsControl(character) ? '�' : character));
    }
}
