﻿using System;
using System.IO;
using kafka4net.Utils;
using LZ4;
using xxHashSharp;

namespace kafka4net.Compression
{
    /// <summary>
    /// Lz4 spec: https://docs.google.com/document/d/1Tdxmn5_2e5p1y4PtXkatLndWVb0R8QARJFe6JI4Keuo/edit
    /// Kafka use the following settings:
    ///     Version: 01
    ///     Block independence: 1
    ///     Block checksum: 0
    ///     Content size: 0
    ///     Content checksum: 0
    ///     Preset dictionary: 0
    ///     Block maximum size: 64Kb
    /// </summary>
    class Lz4KafkaStream : Stream
    {
        Stream _base;
        CompressionStreamMode _mode;
        readonly byte[] _headerBuffer = new byte[LZ4_MAX_HEADER_LENGTH];
        byte[] _blockBuffer;
        byte[] _uncompressedBuffer;
        static readonly int[] _maxBlockSizeTable = {0,0,0,0, 64*1024, 256*1024, 1024*1024, 4*1024*1024 };
        static readonly byte[] _zero32 = { 0, 0, 0, 0 };

        const int LZ4_MAX_HEADER_LENGTH = 19;
        const uint MAGIC = 0x184D2204;
        static readonly byte[] _frameDescriptor = CreateHeader();
        xxHash _hasher = new xxHash();
        int _bufferLen;
        int _bufferPtr;
        bool _isComplete;
        int _blockSize = 64 * 1024;
        bool _eofWritten;

        public Lz4KafkaStream(Stream @base, CompressionStreamMode mode)
        {
            _base = @base;
            _mode = mode;

            if (mode == CompressionStreamMode.Decompress)
            {
                if (!ReadHeader())
                    throw new InvalidDataException("Failed to read lz4 header");

                ReadBlock();
            }
            else
            {
                WriteHeader();
            }
        }

        public override void Flush()
        {
            // TODO: memory optimization
            var compressedBlock = new byte[LZ4Codec.MaximumOutputLength(_blockSize)];
            var compressedSize = LZ4Codec.Encode(_blockBuffer, 0, _bufferLen, compressedBlock, 0, compressedBlock.Length);

            if (compressedSize >= _bufferLen)
            {
                // Write non-compressed
                // TODO: avoid allocation
                var buff4 = new byte[4];
                LittleEndianConverter.Write((uint)(_bufferLen | 1 << 31), buff4, 0); // highest bit set indicates no compression
                _base.Write(buff4, 0, 4);
                _base.Write(_blockBuffer, 0, _bufferLen);
            }
            else
            {
                LittleEndianConverter.Write((uint)compressedSize, _blockBuffer, 0);
                _base.Write(_blockBuffer, 0, 4);
                _base.Write(compressedBlock, 0, compressedSize);
            }

            _bufferLen = 0;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_bufferPtr < _bufferLen)
                return ReadInternalBuffer(buffer, offset, count);

            if (_isComplete)
                return 0;

            if (!ReadBlock())
                return 0;

            return ReadInternalBuffer(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_blockBuffer == null)
                _blockBuffer = new byte[_blockSize];

            while(count > 0)
            {
                // TODO: if full frame is going to be flushed, skip array copy and compress stright from input buffer
                var size = Math.Min(count, _blockSize - _bufferLen);
                Buffer.BlockCopy(buffer, offset, _blockBuffer, _bufferLen, size);
                _bufferLen += size;
                count -= size;
                offset += size;
                
                if(_bufferLen == _blockSize)
                    Flush();
            }
        }

        public override bool CanRead => _mode == CompressionStreamMode.Decompress;
        public override bool CanSeek => false;
        public override bool CanWrite => _mode == CompressionStreamMode.Compress;
        public override long Length { get { throw new NotImplementedException(); } }
        public override long Position { get { throw new NotImplementedException(); } set { throw new NotImplementedException(); } }

        //
        // Implementation
        //

        bool ReadHeader()
        {
            // Read magic, FLG/BD and Descriptor Checksum
            if (!StreamUtils.ReadAll(_base, _headerBuffer, 7))
                return false;

            if(LittleEndianConverter.ReadUInt32(_headerBuffer, 0) != MAGIC)
                throw new InvalidDataException("Invalid lz4 magic");

            // parse FLG
            var flg = _headerBuffer[4];
            var version = flg >> 6;
            if (version != 1)
                throw new InvalidDataException($"Unsupported version of LZ4 format. Supported 1 but got {version}");

            var hasBlockChecksum = (flg & (1 << 4)) != 0;
            if(hasBlockChecksum)
                throw new NotImplementedException("Block checksum is not implemented");


            // parse BD and allocate uncompressed buffer
            var bd = _headerBuffer[5];
            var maxBlockSizeIndex = (bd >> 4) & 0x7;
            if(maxBlockSizeIndex >= _maxBlockSizeTable.Length)
                throw new InvalidDataException($"Invalid LZ4 max data block size index: {maxBlockSizeIndex}");
            int maxBlockSize = _maxBlockSizeTable[maxBlockSizeIndex];
            if(maxBlockSize == 0)
                throw new InvalidDataException($"Invalid LZ4 max data block size index: {maxBlockSizeIndex}");
            _uncompressedBuffer = new byte[maxBlockSize];

            _hasher.Init();
            // Yep, this is the bug in kafka's framing checksum KAFKA-3160. Magic should not be checksummed but it is
            _hasher.Update(_headerBuffer, 6);   // Will need to patch it to accept offset in order to avoid unneeded reallocations, 
                                                // when want to exlude magic
            var calculatedChecksum = (_hasher.Digest() >> 8) & 0xff;

            if(calculatedChecksum != _headerBuffer[6])
                throw new InvalidDataException("Lz4 Frame Descriptor checksum mismatch");

            return true;
        }

        // TODO: for optimization, hardcode the whole header as static array
        static byte[] CreateHeader()
        {
            var buff = new byte[4 + 3];

            LittleEndianConverter.Write(MAGIC, buff, 0);
            // Version 1; Block Independence
            var flags = 1 << 6 | 1 << 5; //96;
            // TODO: make lz4 block size configurable
            // Block size 64Kb
            var bd = 1 << 6; //64;

            buff[4] = (byte)flags;
            buff[5] = (byte)bd;

            var hasher = new xxHash();
            hasher.Init();
            hasher.Update(buff, 6);  
            var checksum = hasher.Digest() >> 8 & 0xff; // 26
            buff[6] = (byte)checksum;

            return buff;
        }

        void WriteHeader()
        {
            _base.Write(_frameDescriptor, 0, _frameDescriptor.Length);
        }

        bool ReadBlock()
        {
            // Reuse _uncompressedBuffer to read block size uint32
            if (!StreamUtils.ReadAll(_base, _uncompressedBuffer, 4))
                throw new InvalidDataException("Unexpected end of LZ4 data block");
            var blockSize = (int)LittleEndianConverter.ReadUInt32(_uncompressedBuffer, 0);
            if (blockSize == 0)
            {
                _isComplete = true;
                return false;
            }

            var isCompressed = (blockSize & 0x80000000) == 0;
            blockSize = blockSize & 0x7fffffff;

            if (_blockBuffer == null || _blockBuffer.Length < blockSize)
                _blockBuffer = new byte[Math.Max(blockSize, 16*1024)];

            if(!StreamUtils.ReadAll(_base, _blockBuffer, blockSize))
                throw new InvalidDataException("Unexpected end of LZ4 data block");

            // Ignore block checksum because kafka does not set it

            if (!isCompressed)
            {
                _blockBuffer.CopyTo(_uncompressedBuffer, 0);
                _bufferLen = blockSize;
                _bufferPtr = 0;
                return true;
            }


            var decodedSize = LZ4Codec.Decode(_blockBuffer, 0, blockSize, _uncompressedBuffer, 0, _uncompressedBuffer.Length);

            _bufferLen = decodedSize;
            _bufferPtr = 0;

            return true;
        }

        int ReadInternalBuffer(byte[] buffer, int offset, int count)
        {
            var canRead = Math.Min(count, _bufferLen - _bufferPtr);
            Buffer.BlockCopy(_uncompressedBuffer, _bufferPtr, buffer, offset, canRead);
            _bufferPtr += canRead;
            return canRead;
        }

        protected override void Dispose(bool disposing)
        {
            if (_mode == CompressionStreamMode.Compress && _blockBuffer != null && _bufferLen != 0)
                Flush();

            if (_mode == CompressionStreamMode.Compress && !_eofWritten)
                WriteEof();

            base.Dispose(disposing);
        }

        void WriteEof()
        {
            _base.Write(_zero32, 0, 4);
            _eofWritten = true;
        }

    }
}
