﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.Http2.Protocol.Exceptions;
using Microsoft.Http2.Protocol.Extensions;

namespace Microsoft.Http2.Protocol.Compression.HeadersDeltaCompression
{
    //This headers compression algorithm is described in
    // http://tools.ietf.org/html/draft-ietf-httpbis-header-compression-05
    /// <summary>
    /// This class implement header compression.
    /// </summary>
    internal partial class CompressionProcessor : ICompressionProcessor
    {
        private const int MaxHeaderByteSize = 4096;

        private HeadersList _headerTable;
        private HeadersList _refSet;
        private bool _isDisposed;

        private MemoryStream _serializerStream;

        public CompressionProcessor()
        {
            //05 The header table is initially empty.
            _headerTable = new HeadersList();

            //05 The reference set is initially empty.
            _refSet = new HeadersList();

            InitCompressor();
            InitDecompressor();
        }

        private void InitCompressor()
        {
            _serializerStream = new MemoryStream();
        }

        private void InitDecompressor()
        {
            _currentOffset = 0;
        }

        /// <summary>
        /// Modifies the table.
        /// </summary>
        /// <param name="headerName">Name of the header.</param>
        /// <param name="headerValue">The header value.</param>
        /// <param name="headerType">Type of the header.</param>
        /// <param name="useHeadersTable">The use headers table.</param>
        /// <param name="index">The index.</param>
        private void ModifyTable(string headerName, string headerValue, IndexationType headerType)
        {
            //spec 05
            // The size of an entry is the sum of its name's length in octets (as
            //defined in Section 4.1.2), of its value's length in octets
            //(Section 4.1.2) and of 32 octets.
            int headerLen = headerName.Length + headerValue.Length + 32;

            //spec 05
            //To limit the memory requirements on the decoder side, the size of the
            //header table is bounded.  The size of the header table MUST stay
            //lower than or equal to the value of the HTTP/2.0 setting
            //SETTINGS_HEADER_TABLE_SIZE (see [HTTP2]).
            switch (headerType)
            {
                case IndexationType.Incremental:

                    //spec 05
                    //3.3.3. Entry Eviction when Adding New Entries
                    //Whenever a new entry is to be added to the table, any name referenced
                    //by the representation of this new entry is cached, and then entries
                    //are evicted from the end of the header table until the size of the
                    //header table is less than or equal to SETTINGS_HEADER_TABLE_SIZE -
                    //new entry size, or until the table is empty.

                    //If the size of the new entry is less than or equal to
                    //SETTINGS_HEADER_TABLE_SIZE, that entry is added to the table.  It is
                    //not an error to attempt to add an entry that is larger than
                    //SETTINGS_HEADER_TABLE_SIZE.

                    while (_headerTable.StoredHeadersSize + headerLen >= MaxHeaderByteSize && _headerTable.Count > 0)
                    {
                        _headerTable.RemoveAt(_headerTable.Count - 1);

                        //spec 05
                        //3.3.2. Entry Eviction When Header Table Size Changes
                        //Whenever an entry is evicted from the header table, any reference to
                        //that entry contained by the reference set is removed.
                        var header = new KeyValuePair<string, string>(headerName, headerValue);
                        if (_refSet.Contains(header))
                            _refSet.Remove(header);
                    }

                    _headerTable.Add(new KeyValuePair<string, string>(headerName, headerValue));
                    break;
                default:
                    return;
            }
        }

        #region Compression

        private byte[] EncodeString(string item, bool useHuffman = false)
        {
            byte[] itemBts = null;
            int len = item.Length;
            const int prefix = 7;
            byte[] lenBts = len.ToUVarInt(prefix); //05: String representation | H |  Value Length Prefix (7)  |

            if (!useHuffman)
            {
                itemBts = Encoding.UTF8.GetBytes(item);
            }
            else
            {
                //TODO compr with huffman
                lenBts[0] |= 0x80; //05: Set huffman to true | 1 |  Value Length Prefix (7)  |
            }

            byte[] result = new byte[lenBts.Length + itemBts.Length];
            Buffer.BlockCopy(lenBts, 0 , result, 0, len);
            Buffer.BlockCopy(itemBts, len, result, 0, itemBts.Length);

            return result;
        }

        private void CompressHeader(KeyValuePair<string, string> header, IAdditionalHeaderInfo type)
        {
            byte prefix = 0;
            var headerType = (type as Indexation).Type;

            switch (headerType)
            {
                case IndexationType.WithoutIndexation:
                case IndexationType.Incremental:
                    prefix = 6;
                    break;
                case IndexationType.Indexed:
                    CompressIndexed(header);
                    return;
            }

            CompressNonIndexed(header.Key, header.Value, headerType, prefix);
        }

        private void CompressNonIndexed(string headerName, string headerValue, IndexationType headerType, byte prefix)
        {
            //spec 05
            //05 does not tell anything about case_sensitive | insensitive
            int index = _headerTable.FindIndex(kv => kv.Key.Equals(headerName));
            bool isFound = index != -1;

            /* 05 spec:
            <-- Header  Table -->  <-- Static  Table -->
            +---+-----------+---+  +---+-----------+---+
            | 1 |    ...    | k |  |k+1|    ...    | n |
            +---+-----------+---+  +---+-----------+---+
            ^                   |
            |                   V
            Insertion Point       Drop Point
             */
            if (!isFound)
            {
                index = _staticTable.FindIndex(kv => kv.Key.Equals(headerName));
                isFound = index != -1;

                if (isFound)
                {
                    index += _headerTable.Count;

                    //3.2.1. Header Field Representation Processing
                    //The referenced static entry is inserted at the beginning of the
                    //header table.
                    _headerTable.Insert(0, new KeyValuePair<string, string>(headerName, headerValue));
                }
            }
            //It's necessary to form result array because partial writeToOutput stream can cause problems because of multithreading
            using (var stream = new MemoryStream(64))
            {
                byte[] indexBinary;
                byte[] nameBinary = new byte[0];
                byte[] valueBinary;

                if (isFound)
                {
                    //Header key was found in the header table. Hence we should encode only value
                    indexBinary = (index + 1).ToUVarInt(prefix);
                    valueBinary = EncodeString(headerValue, false); 
                }
                else
                {
                    //Header key was not found in the header table. Hence we should encode name and value
                    indexBinary = 0.ToUVarInt(prefix);
                    nameBinary = EncodeString(headerName, false);
                    valueBinary = EncodeString(headerValue, false);
                }
                
                //Set without index type
                indexBinary[0] |= (byte)headerType;

                stream.Write(indexBinary, 0, indexBinary.Length);
                stream.Write(nameBinary, 0, nameBinary.Length);
                stream.Write(valueBinary, 0, valueBinary.Length);

                WriteToOutput(stream.GetBuffer(), 0, (int)stream.Position);
            }

            ModifyTable(headerName, headerValue, headerType);
        }

        private void CompressIndexed(KeyValuePair<string, string> header)
        {
            //spec 05
            //nothing told about case_sensitive | _insensitive comparsion
            int index = _headerTable.FindIndex(kv => kv.Key.Equals(header.Key));
            bool isFound = index != -1;

            /* 05 spec:
            <-- Header  Table -->  <-- Static  Table -->
            +---+-----------+---+  +---+-----------+---+
            | 1 |    ...    | k |  |k+1|    ...    | n |
            +---+-----------+---+  +---+-----------+---+
            ^                   |
            |                   V
            Insertion Point       Drop Point
             */
            if (!isFound)
            {
                index = _staticTable.FindIndex(kv => kv.Key.Equals(header.Value));
                isFound = index != -1;

                if (isFound)
                {
                    index += _headerTable.Count;

                    //3.2.1. Header Field Representation Processing
                    //The referenced static entry is inserted at the beginning of the
                    //header table.
                    _headerTable.Insert(0, new KeyValuePair<string, string>(header.Key, header.Value));
                }
            }

            if (!isFound)
            {
                throw new CompressionError(new Exception("cant compress indexed header. Index not found."));
            }

            const byte prefix = 7;
            var bytes = (index + 1).ToUVarInt(prefix);

            //Set indexed type
            bytes[0] |= (byte) IndexationType.Indexed;

            WriteToOutput(bytes, 0, bytes.Length);
        }

        public byte[] Compress(HeadersList headers)
        {
            var toSend = new HeadersList();
            var toDelete = new HeadersList(_refSet);

            ClearStream(_serializerStream, (int) _serializerStream.Position);

            foreach (var header in headers)
            {
                if (header.Key == null || header.Value == null)
                {
                    throw new InvalidHeaderException(header);
                }
                if (!_refSet.Contains(header))
                {
                    //Not there, Will send
                    toSend.Add(header);
                }
                else
                {
                    //Already there, don't delete
                    toDelete.Remove(header);
                }
            }
            foreach (var header in toDelete)
            {
                //Anything left in toDelete, should send, so it is deleted from ref set.
                CompressIndexed(header);
                _refSet.Remove(header); //Update our copy
            }
            foreach (var header in toSend)
            {
                //Send whatever was left in headersCopy
                if (_headerTable.Contains(header))
                {
                    CompressIndexed(header);
                }
                else
                {
                    CompressHeader(header, new Indexation(IndexationType.Incremental));
                }
                _refSet.Add(header); //Update our copy
            }

            _serializerStream.Flush();
            var result = new byte[_serializerStream.Position];

            var streamBuffer = _serializerStream.GetBuffer();

            Buffer.BlockCopy(streamBuffer, 0, result, 0, (int)_serializerStream.Position);

            return result;
        }

        #endregion

        #region Decompression

        private int _currentOffset;

        private Tuple<string, string, IndexationType> ProcessIndexed(int index)
        {
            //An _indexed representation_ with an index value of 0 entails the
            //following actions:
            //o  The reference set is emptied.
            if (index == 0)
            {
                _refSet.Clear();
                return default(Tuple<string, string, IndexationType>);
            }

            var kv = default(KeyValuePair<string, string>);
            //o  If referencing an element of the static table:
            if (index > _headerTable.Count)
            {
                if (index <= _headerTable.Count + _staticTable.Count)
                {
                    kv = _staticTable[index - _headerTable.Count - 1];

                    //*  The referenced static entry is inserted at the beginning of the
                    //header table.
                    _headerTable.Insert(0, kv);

                    //*  A reference to this new header table entry is added to the
                    //reference set (except if this new entry didn't fit in the
                    //header table).
                    _refSet.Add(kv);

                    //*  The header field corresponding to the referenced entry is
                    //emitted.
                    return new Tuple<string, string, IndexationType>(kv.Key, kv.Value, IndexationType.Indexed);
                }

                throw new IndexOutOfRangeException("Index not found in the static table");
            }

            //o  If referencing an element of the header table:
            kv = _headerTable[index - 1];

            //*  The referenced header table entry is added to the reference
            //set.
            _refSet.Add(kv);
            //*  The header field corresponding to the referenced entry is
            //emitted.
            return new Tuple<string, string, IndexationType>(kv.Key, kv.Value, IndexationType.Indexed);
        }

        private Tuple<string, string, IndexationType> ParseHeader(byte[] bytes)
        {
            var type = GetHeaderType(bytes);
            int index = GetIndex(bytes, type);
            string name;
            string value;
            byte valueLen;
            byte nameLen;

            switch (type)
            {
                case IndexationType.Indexed:
                    return ProcessIndexed(index);

                case IndexationType.Incremental:
                case IndexationType.WithoutIndexation:

                    if (index == 0)
                    {
                        nameLen = bytes[_currentOffset++];
                        name = Encoding.UTF8.GetString(bytes, _currentOffset, nameLen);
                        _currentOffset += nameLen;
                    }
                    else
                    {
                        //Index increased by 1 was sent
                        name = _headerTable[index - 1].Key;
                    }

                    valueLen = bytes[_currentOffset++];
                    value = Encoding.UTF8.GetString(bytes, _currentOffset, valueLen);
                    _currentOffset += valueLen;

                    ModifyTable(name, value, type);

                    return new Tuple<string, string, IndexationType>(name, value, type);
            }

            return default(Tuple<string, string, IndexationType>);
        }
        
        private int GetIndex(byte[] bytes, IndexationType type)
        {
            byte prefix = 0;
            byte firstByteValue = bytes[_currentOffset];

            switch (type)
            {  
                case IndexationType.Incremental:
                case IndexationType.WithoutIndexation:
                    prefix = 6;
                    break;
                case IndexationType.Indexed:
                    prefix = 7;
                    break;
            }
            int maxPrefixVal = (1 << prefix) - 1;

            if (firstByteValue < maxPrefixVal)
            {
                _currentOffset++;
                return firstByteValue;
            }

            int i = 1;
            while (true)
            {
                if ((bytes[_currentOffset + i] & 0x80) == 0)
                {
                    break;
                }
                i++;
            }

            var numberBytes = new byte[++i];
            Buffer.BlockCopy(bytes, _currentOffset, numberBytes, 0, i);
            _currentOffset += i;

            return Int32Extensions.FromUVarInt(numberBytes);
        }

        private IndexationType GetHeaderType(byte[] bytes)
        {
            var typeByte = bytes[_currentOffset];
            IndexationType indexationType;
            
            if ((typeByte & 0x80) == (byte)IndexationType.Indexed)
            {
                indexationType = IndexationType.Indexed;
            }
            else if ((typeByte & 0x40) == (byte)IndexationType.WithoutIndexation)
            {
                indexationType = IndexationType.WithoutIndexation;
            }
            else
            {
                indexationType = IndexationType.Incremental;
            }
            //throw type mask away
            bytes[_currentOffset] = (byte)(bytes[_currentOffset] & (~(byte)indexationType));
            return indexationType;
        }

        public HeadersList Decompress(byte[] serializedHeaders)
        {
            try
            {
                var workingSet = new HeadersList(_refSet);
                var unindexedHeadersList = new HeadersList();
                _currentOffset = 0;

                while (_currentOffset != serializedHeaders.Length)
                {
                    var entry = ParseHeader(serializedHeaders);

                    if (entry.Equals(default(Tuple<string, string, IndexationType>)) && entry.Item3 == IndexationType.Indexed)
                        continue;

                    var header = new KeyValuePair<string, string>(entry.Item1, entry.Item2);

                    if (entry.Item3 == IndexationType.Indexed)
                    {
                        if (workingSet.Contains(header))
                            workingSet.RemoveAll(h => h.Equals(header));
                        else
                            workingSet.Add(header);
                    }
                    else if (entry.Item3 == IndexationType.WithoutIndexation)
                    {
                        unindexedHeadersList.Add(header);
                    }
                    else
                    {
                        workingSet.Add(header);
                    }
                }

                _refSet = new HeadersList(workingSet);

                for (int i = _headerTable.Count - 1; i >= 0; --i)
                {
                    var header = _refSet[i];
                    if (!_headerTable.Contains(header))
                        _refSet.RemoveAll(h => h.Equals(header));
                }

                workingSet.AddRange(unindexedHeadersList);
                return workingSet;
            }
            catch (Exception e)
            {
                throw new CompressionError(e);
            }
        }

        #endregion 

        private void WriteToOutput(byte[] bytes, int offset, int length)
        {
            _serializerStream.Write(bytes, offset, length);
        }

        private static void ClearStream(Stream input, int len)
        {
            var buffer = new byte[len];
            input.Position = 0;
            input.Write(buffer, 0, len);
            input.SetLength(0);
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _serializerStream.Dispose(); 
            _isDisposed = true;
        }

    }
}
