using System;
using System.IO;
using System.Text;
using SevenZip;
using LzmaEncoder = SevenZip.Compression.LZMA.Encoder;

namespace UpdateClient.Compression
{
    internal interface ILogArchiveCompressor
    {
        void CompressToArchive(string sourcePath, string archivePath);
    }

    internal sealed class ManagedSevenZipLogArchiveCompressor : ILogArchiveCompressor
    {
        private const int DefaultDictionarySize = 1 << 20;
        private const int MinDictionarySize = 1 << 16;
        private const int MaxDictionarySize = 1 << 24;

        private static readonly CoderPropID[] PropertyIds =
        {
            CoderPropID.DictionarySize,
            CoderPropID.PosStateBits,
            CoderPropID.LitContextBits,
            CoderPropID.LitPosBits,
            CoderPropID.Algorithm,
            CoderPropID.NumFastBytes,
            CoderPropID.MatchFinder,
            CoderPropID.EndMarker
        };

        public void CompressToArchive(string sourcePath, string archivePath)
        {
            ValidateCompressionArguments(sourcePath, archivePath);

            FileInfo sourceFileInfo = new FileInfo(sourcePath);
            if (!sourceFileInfo.Exists)
            {
                throw new FileNotFoundException("Source log file was not found.", sourcePath);
            }

            int dictionarySize = GetDictionarySize(sourceFileInfo.Length);
            LzmaEncoder encoder = CreateEncoder(dictionarySize);
            byte[] coderProperties = GetCoderProperties(encoder);

            using (FileStream archiveStream = new FileStream(archivePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                SevenZipArchiveWriter.WriteSingleFileArchive(
                    sourcePath,
                    sourceFileInfo.Name,
                    sourceFileInfo.Length,
                    sourceFileInfo.LastWriteTimeUtc,
                    encoder,
                    coderProperties,
                    archiveStream);
            }
        }

        private static void ValidateCompressionArguments(string sourcePath, string archivePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
            {
                throw new ArgumentException("Source path is required.", nameof(sourcePath));
            }

            if (string.IsNullOrWhiteSpace(archivePath))
            {
                throw new ArgumentException("Archive path is required.", nameof(archivePath));
            }
        }

        private static LzmaEncoder CreateEncoder(int dictionarySize)
        {
            LzmaEncoder encoder = new LzmaEncoder();
            object[] propertyValues =
            {
                dictionarySize,
                2,
                3,
                0,
                2,
                128,
                "BT4",
                false
            };

            encoder.SetCoderProperties(PropertyIds, propertyValues);
            return encoder;
        }

        private static byte[] GetCoderProperties(IWriteCoderProperties encoder)
        {
            using (MemoryStream propertyStream = new MemoryStream())
            {
                encoder.WriteCoderProperties(propertyStream);
                return propertyStream.ToArray();
            }
        }

        private static int GetDictionarySize(long fileSize)
        {
            if (fileSize <= 0)
            {
                return DefaultDictionarySize;
            }

            long candidate = MinDictionarySize;
            while (candidate < fileSize && candidate < MaxDictionarySize)
            {
                candidate <<= 1;
            }

            if (candidate > MaxDictionarySize)
            {
                candidate = MaxDictionarySize;
            }

            return (int)candidate;
        }
    }

    internal static class SevenZipArchiveWriter
    {
        private static readonly byte[] SignatureHeader =
        {
            0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04
        };

        private static readonly byte[] LzmaMethodId =
        {
            0x03, 0x01, 0x01
        };

        private static class HeaderNid
        {
            public const byte End = 0x00;
            public const byte Header = 0x01;
            public const byte MainStreamsInfo = 0x04;
            public const byte FilesInfo = 0x05;
            public const byte PackInfo = 0x06;
            public const byte UnpackInfo = 0x07;
            public const byte SubStreamsInfo = 0x08;
            public const byte Size = 0x09;
            public const byte Crc = 0x0A;
            public const byte Folder = 0x0B;
            public const byte CodersUnpackSize = 0x0C;
            public const byte Name = 0x11;
            public const byte MTime = 0x14;
        }

        public static void WriteSingleFileArchive(
            string sourcePath,
            string entryName,
            long unpackedSize,
            DateTime lastWriteTimeUtc,
            LzmaEncoder encoder,
            byte[] coderProperties,
            Stream archiveStream)
        {
            if (archiveStream == null)
            {
                throw new ArgumentNullException(nameof(archiveStream));
            }

            if (!archiveStream.CanSeek || !archiveStream.CanWrite)
            {
                throw new InvalidOperationException("Archive stream must support seek and write operations.");
            }

            ArchiveMetadata metadata = CompressSourceStream(
                sourcePath,
                entryName,
                unpackedSize,
                lastWriteTimeUtc,
                encoder,
                coderProperties,
                archiveStream);

            byte[] nextHeader = BuildHeader(metadata);
            uint nextHeaderCrc = CalculateCrc(nextHeader);

            archiveStream.Write(nextHeader, 0, nextHeader.Length);
            WriteSignatureHeader(archiveStream, metadata.PackedSize, (ulong)nextHeader.Length, nextHeaderCrc);
            archiveStream.Flush();
        }

        private static ArchiveMetadata CompressSourceStream(
            string sourcePath,
            string entryName,
            long unpackedSize,
            DateTime lastWriteTimeUtc,
            LzmaEncoder encoder,
            byte[] coderProperties,
            Stream archiveStream)
        {
            archiveStream.SetLength(0);
            archiveStream.Position = 0;
            archiveStream.Write(new byte[32], 0, 32);

            using (CrcTrackingReadStream inputStream = new CrcTrackingReadStream(
                new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan)))
            {
                CrcTrackingWriteStream packedStream = new CrcTrackingWriteStream(archiveStream);
                encoder.Code(inputStream, packedStream, unpackedSize, -1, null);
                packedStream.Flush();

                return new ArchiveMetadata(
                    entryName,
                    (ulong)unpackedSize,
                    lastWriteTimeUtc.ToFileTimeUtc(),
                    coderProperties,
                    packedStream.TotalBytesWritten,
                    packedStream.Digest,
                    inputStream.Digest);
            }
        }

        private static byte[] BuildHeader(ArchiveMetadata metadata)
        {
            using (MemoryStream headerStream = new MemoryStream())
            {
                headerStream.WriteByte(HeaderNid.Header);
                WriteStreamsInfo(headerStream, metadata);
                WriteFilesInfo(headerStream, metadata);
                headerStream.WriteByte(HeaderNid.End);
                return headerStream.ToArray();
            }
        }

        private static void WriteStreamsInfo(Stream stream, ArchiveMetadata metadata)
        {
            stream.WriteByte(HeaderNid.MainStreamsInfo);
            WritePackInfo(stream, metadata);
            WriteUnpackInfo(stream, metadata);
            WriteSubStreamsInfo(stream);
            stream.WriteByte(HeaderNid.End);
        }

        private static void WritePackInfo(Stream stream, ArchiveMetadata metadata)
        {
            stream.WriteByte(HeaderNid.PackInfo);
            WriteEncodedUInt64(stream, 0);
            WriteEncodedUInt64(stream, 1);

            stream.WriteByte(HeaderNid.Size);
            WriteEncodedUInt64(stream, metadata.PackedSize);

            stream.WriteByte(HeaderNid.Crc);
            stream.WriteByte(1);
            WriteUInt32LittleEndian(stream, metadata.PackedCrc);
            stream.WriteByte(HeaderNid.End);
        }

        private static void WriteUnpackInfo(Stream stream, ArchiveMetadata metadata)
        {
            stream.WriteByte(HeaderNid.UnpackInfo);

            stream.WriteByte(HeaderNid.Folder);
            WriteEncodedUInt64(stream, 1);
            stream.WriteByte(0);
            WriteFolder(stream, metadata);

            stream.WriteByte(HeaderNid.CodersUnpackSize);
            WriteEncodedUInt64(stream, metadata.UnpackedSize);

            stream.WriteByte(HeaderNid.Crc);
            stream.WriteByte(1);
            WriteUInt32LittleEndian(stream, metadata.UnpackedCrc);
            stream.WriteByte(HeaderNid.End);
        }

        private static void WriteFolder(Stream stream, ArchiveMetadata metadata)
        {
            WriteEncodedUInt64(stream, 1);
            stream.WriteByte((byte)(LzmaMethodId.Length | 0x20));
            stream.Write(LzmaMethodId, 0, LzmaMethodId.Length);
            WriteEncodedUInt64(stream, (ulong)metadata.CoderProperties.Length);
            stream.Write(metadata.CoderProperties, 0, metadata.CoderProperties.Length);
        }

        private static void WriteSubStreamsInfo(Stream stream)
        {
            stream.WriteByte(HeaderNid.SubStreamsInfo);
            stream.WriteByte(HeaderNid.End);
        }

        private static void WriteFilesInfo(Stream stream, ArchiveMetadata metadata)
        {
            stream.WriteByte(HeaderNid.FilesInfo);
            WriteEncodedUInt64(stream, 1);
            WriteNameProperty(stream, metadata.EntryName);
            WriteModifiedTimeProperty(stream, metadata.LastWriteTimeFileTime);
            stream.WriteByte(HeaderNid.End);
        }

        private static void WriteNameProperty(Stream stream, string entryName)
        {
            byte[] nameData = Encoding.Unicode.GetBytes((entryName ?? string.Empty) + "\0");
            stream.WriteByte(HeaderNid.Name);
            WriteEncodedUInt64(stream, (ulong)(1 + nameData.Length));
            stream.WriteByte(0);
            stream.Write(nameData, 0, nameData.Length);
        }

        private static void WriteModifiedTimeProperty(Stream stream, long fileTime)
        {
            stream.WriteByte(HeaderNid.MTime);
            WriteEncodedUInt64(stream, 10);
            stream.WriteByte(1);
            stream.WriteByte(0);
            WriteUInt64LittleEndian(stream, unchecked((ulong)fileTime));
        }

        private static void WriteSignatureHeader(Stream stream, ulong nextHeaderOffset, ulong nextHeaderSize, uint nextHeaderCrc)
        {
            using (MemoryStream startHeaderStream = new MemoryStream())
            {
                WriteUInt64LittleEndian(startHeaderStream, nextHeaderOffset);
                WriteUInt64LittleEndian(startHeaderStream, nextHeaderSize);
                WriteUInt32LittleEndian(startHeaderStream, nextHeaderCrc);

                byte[] startHeaderBytes = startHeaderStream.ToArray();
                uint startHeaderCrc = CalculateCrc(startHeaderBytes);

                stream.Position = 0;
                stream.Write(SignatureHeader, 0, SignatureHeader.Length);
                WriteUInt32LittleEndian(stream, startHeaderCrc);
                stream.Write(startHeaderBytes, 0, startHeaderBytes.Length);
            }
        }

        private static void WriteEncodedUInt64(Stream stream, ulong value)
        {
            for (int additionalByteCount = 0; additionalByteCount < 8; additionalByteCount++)
            {
                int availableBits = 7 * (additionalByteCount + 1);
                if (value < (1UL << availableBits))
                {
                    byte firstByte = (byte)(0xFF << (8 - additionalByteCount));
                    firstByte |= (byte)(value >> (8 * additionalByteCount));
                    stream.WriteByte(firstByte);

                    for (int index = 0; index < additionalByteCount; index++)
                    {
                        stream.WriteByte((byte)(value & 0xFF));
                        value >>= 8;
                    }

                    return;
                }
            }

            stream.WriteByte(0xFF);
            for (int index = 0; index < 8; index++)
            {
                stream.WriteByte((byte)(value & 0xFF));
                value >>= 8;
            }
        }

        private static void WriteUInt32LittleEndian(Stream stream, uint value)
        {
            for (int index = 0; index < 4; index++)
            {
                stream.WriteByte((byte)(value & 0xFF));
                value >>= 8;
            }
        }

        private static void WriteUInt64LittleEndian(Stream stream, ulong value)
        {
            for (int index = 0; index < 8; index++)
            {
                stream.WriteByte((byte)(value & 0xFF));
                value >>= 8;
            }
        }

        private static uint CalculateCrc(byte[] buffer)
        {
            CRC crc = new CRC();
            crc.Update(buffer, 0, (uint)buffer.Length);
            return crc.GetDigest();
        }

        private sealed class ArchiveMetadata
        {
            public ArchiveMetadata(
                string entryName,
                ulong unpackedSize,
                long lastWriteTimeFileTime,
                byte[] coderProperties,
                ulong packedSize,
                uint packedCrc,
                uint unpackedCrc)
            {
                EntryName = entryName;
                UnpackedSize = unpackedSize;
                LastWriteTimeFileTime = lastWriteTimeFileTime;
                CoderProperties = coderProperties;
                PackedSize = packedSize;
                PackedCrc = packedCrc;
                UnpackedCrc = unpackedCrc;
            }

            public string EntryName { get; private set; }

            public ulong UnpackedSize { get; private set; }

            public long LastWriteTimeFileTime { get; private set; }

            public byte[] CoderProperties { get; private set; }

            public ulong PackedSize { get; private set; }

            public uint PackedCrc { get; private set; }

            public uint UnpackedCrc { get; private set; }
        }

        private sealed class CrcTrackingReadStream : Stream
        {
            private readonly Stream innerStream;
            private readonly CRC crc = new CRC();

            public CrcTrackingReadStream(Stream stream)
            {
                innerStream = stream ?? throw new ArgumentNullException(nameof(stream));
            }

            public uint Digest
            {
                get { return crc.GetDigest(); }
            }

            public override bool CanRead
            {
                get { return innerStream.CanRead; }
            }
            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return false; }
            }

            public override long Length
            {
                get { return innerStream.Length; }
            }

            public override long Position
            {
                get { return innerStream.Position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                int bytesRead = innerStream.Read(buffer, offset, count);
                if (bytesRead > 0)
                {
                    crc.Update(buffer, (uint)offset, (uint)bytesRead);
                }

                return bytesRead;
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    innerStream.Dispose();
                }

                base.Dispose(disposing);
            }
        }

        private sealed class CrcTrackingWriteStream : Stream
        {
            private readonly Stream innerStream;
            private readonly CRC crc = new CRC();
            private ulong totalBytesWritten;

            public CrcTrackingWriteStream(Stream stream)
            {
                innerStream = stream ?? throw new ArgumentNullException(nameof(stream));
            }

            public uint Digest
            {
                get { return crc.GetDigest(); }
            }

            public ulong TotalBytesWritten
            {
                get { return totalBytesWritten; }
            }

            public override bool CanRead
            {
                get { return false; }
            }

            public override bool CanSeek
            {
                get { return false; }
            }

            public override bool CanWrite
            {
                get { return innerStream.CanWrite; }
            }

            public override long Length
            {
                get { return innerStream.Length; }
            }

            public override long Position
            {
                get { return innerStream.Position; }
                set { throw new NotSupportedException(); }
            }

            public override void Flush()
            {
                innerStream.Flush();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                throw new NotSupportedException();
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                throw new NotSupportedException();
            }

            public override void SetLength(long value)
            {
                throw new NotSupportedException();
            }

            public override void Write(byte[] buffer, int offset, int count)
            {
                innerStream.Write(buffer, offset, count);
                if (count > 0)
                {
                    crc.Update(buffer, (uint)offset, (uint)count);
                    totalBytesWritten += (ulong)count;
                }
            }

            public override void WriteByte(byte value)
            {
                innerStream.WriteByte(value);
                crc.UpdateByte(value);
                totalBytesWritten++;
            }
        }
    }
}

