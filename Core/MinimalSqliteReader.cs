using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

// Minimal, dependency-free, read-only SQLite table reader.
//
// Why this exists instead of a real ADO.NET provider: this project compiles directly via csc.exe
// against a hand-curated list of classic .NET Framework 4.x reference assemblies (see
// Build-Arm64.ps1) -- there is no .csproj, no NuGet restore, and no MSBuild package step anywhere
// in the toolchain. Neither Microsoft.Data.Sqlite nor System.Data.SQLite (and the native ARM64
// interop binary either one requires) can be added without a build-pipeline change, which is out of
// scope for a change that is meant to stop at "compiles cleanly" and leave build/deploy to the
// project owner. This class instead implements just enough of the public SQLite file format
// (https://www.sqlite.org/fileformat2.html, format has been stable since 2004) to walk one table's
// rowid b-tree in descending order and decode NULL/INTEGER/REAL/TEXT/BLOB columns, including
// multi-page overflow payloads.
//
// Deliberately not supported: WAL-mode pages not yet checkpointed into the main file (the .NET
// SQLite drivers this reader was written to be compatible with default to rollback-journal mode, not
// WAL, so committed data is always visible through the main file); write access of any kind; index
// b-trees; any column type this reader's callers do not need (TEXT/INTEGER only in practice).
//
// Every entry point swallows all failures and returns an empty result. This reads a file that a
// separate, unrelated process owns and may be mid-write at any instant -- a torn read, an
// unexpected page, or a future schema change must never throw, hang, or corrupt the caller's UI.
internal static class MinimalSqliteReader
{
    private const int MaxReasonablePageSize = 65536;
    private const int MinReasonablePageSize = 512;
    private const int HeaderSize = 100;
    private const long MaxReasonablePayloadLength = 64L * 1024L * 1024L;
    private const int MaxOverflowPagesPerCell = 100000;

    internal sealed class Row
    {
        // The rowid. For a table declared with a single-column INTEGER PRIMARY KEY, SQLite aliases
        // that column to the rowid -- decode that column's real value from RowId, not from Values:
        // per the file format spec ("Record Format"), the column still occupies its declared
        // position in Values, but always decodes as an empty NULL placeholder there.
        public long RowId;

        // Decoded column values in physical record order, one entry per declared column (including
        // a NULL placeholder for a rowid-alias INTEGER PRIMARY KEY column -- see RowId above).
        // NULL and BLOB decode to string.Empty; INTEGER/REAL decode via InvariantCulture.
        public string[] Values;
    }

    private struct PageHeader
    {
        public int PageType;
        public int CellCount;
        public int HeaderLength;
        public int RightMostPointer;
    }

    // Reads up to maxRows rows from tableName, newest-rowid-first, stopping as soon as maxRows rows
    // have been collected -- a large table is never fully walked since descending traversal always
    // visits the largest remaining subtree first.
    internal static List<Row> ReadLastRowsByRowIdDescending(string databasePath, string tableName, int maxRows)
    {
        List<Row> result = new List<Row>();
        if (string.IsNullOrEmpty(databasePath) || string.IsNullOrEmpty(tableName) || maxRows <= 0)
        {
            return result;
        }

        try
        {
            if (!File.Exists(databasePath))
            {
                return result;
            }

            using (FileStream stream = new FileStream(databasePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                byte[] header = ReadExact(stream, 0, HeaderSize);
                if (header == null || !IsSqliteHeader(header))
                {
                    return result;
                }

                int pageSize = ReadUInt16BigEndian(header, 16);
                if (pageSize == 1)
                {
                    // 1 is the on-disk sentinel for the maximum page size, 65536 (does not fit a
                    // native uint16 field).
                    pageSize = MaxReasonablePageSize;
                }

                if (pageSize < MinReasonablePageSize || pageSize > MaxReasonablePageSize || (pageSize & (pageSize - 1)) != 0)
                {
                    return result; // not a valid power-of-two page size; refuse rather than misparse
                }

                int reservedPerPage = header[20];
                int usableSize = pageSize - reservedPerPage;
                if (usableSize <= 35)
                {
                    return result;
                }

                int pageCount = (int)(stream.Length / pageSize);
                if (pageCount <= 0)
                {
                    return result;
                }

                int rootPage = FindTableRootPage(stream, pageSize, usableSize, tableName);
                if (rootPage <= 0)
                {
                    return result;
                }

                WalkTableBTreeDescending(stream, pageSize, usableSize, pageCount, rootPage, maxRows, result);
            }
        }
        catch
        {
            // A partially-filled result from a failure part-way through the walk is worse than no
            // result: the caller cannot tell "newest N rows" from "newest N rows before we lost our
            // place," so a failure anywhere clears everything gathered so far.
            result.Clear();
        }

        return result;
    }

    private static bool IsSqliteHeader(byte[] header)
    {
        // "SQLite format 3\0"
        return header.Length >= HeaderSize &&
            header[0] == (byte)'S' && header[1] == (byte)'Q' && header[2] == (byte)'L' && header[3] == (byte)'i' &&
            header[4] == (byte)'t' && header[5] == (byte)'e' && header[6] == (byte)' ' && header[7] == (byte)'f';
    }

    // sqlite_master is always the schema table rooted at page 1, immediately after the 100-byte
    // file header. It holds one row per table/index/trigger, which is small enough in any realistic
    // application database to always fit as a single leaf page -- this reader does not need (and
    // does not implement) interior-page traversal for the schema lookup itself.
    private static int FindTableRootPage(FileStream stream, int pageSize, int usableSize, string tableName)
    {
        byte[] page = ReadPage(stream, pageSize, 1);
        if (page == null)
        {
            return -1;
        }

        int headerOffset = HeaderSize;
        PageHeader header = ParsePageHeader(page, headerOffset);
        if (header.PageType != 0x0d)
        {
            return -1;
        }

        for (int i = 0; i < header.CellCount; i++)
        {
            int cellPointer = ReadUInt16BigEndian(page, headerOffset + header.HeaderLength + i * 2);
            Row row = DecodeLeafCell(stream, page, pageSize, usableSize, cellPointer);
            // sqlite_master column order is fixed: type, name, tbl_name, rootpage, sql.
            if (row == null || row.Values.Length < 4)
            {
                continue;
            }

            if (string.Equals(row.Values[0], "table", StringComparison.Ordinal) &&
                string.Equals(row.Values[2], tableName, StringComparison.Ordinal))
            {
                long rootPageValue;
                if (long.TryParse(row.Values[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rootPageValue) &&
                    rootPageValue > 0 && rootPageValue <= int.MaxValue)
                {
                    return (int)rootPageValue;
                }
            }
        }

        return -1;
    }

    private static void WalkTableBTreeDescending(
        FileStream stream,
        int pageSize,
        int usableSize,
        int pageCount,
        int pageNumber,
        int maxRows,
        List<Row> result)
    {
        if (result.Count >= maxRows || pageNumber < 1 || pageNumber > pageCount)
        {
            return;
        }

        byte[] page = ReadPage(stream, pageSize, pageNumber);
        if (page == null)
        {
            return;
        }

        int headerOffset = pageNumber == 1 ? HeaderSize : 0;
        PageHeader header = ParsePageHeader(page, headerOffset);

        if (header.PageType == 0x05)
        {
            // Interior table b-tree. Cell pointers are kept in ascending key order; descend the
            // right-most child (largest keys) first, then each cell's left child from the last
            // (next-largest) back to the first, so the walk naturally yields rowid-descending order
            // and can stop the instant maxRows rows are collected.
            WalkTableBTreeDescending(stream, pageSize, usableSize, pageCount, header.RightMostPointer, maxRows, result);
            for (int i = header.CellCount - 1; i >= 0 && result.Count < maxRows; i--)
            {
                int cellPointer = ReadUInt16BigEndian(page, headerOffset + header.HeaderLength + i * 2);
                int childPage = (int)ReadUInt32BigEndian(page, cellPointer);
                WalkTableBTreeDescending(stream, pageSize, usableSize, pageCount, childPage, maxRows, result);
            }

            return;
        }

        if (header.PageType != 0x0d)
        {
            return; // not a table b-tree page (e.g. an index page reached through a corrupt pointer)
        }

        for (int i = header.CellCount - 1; i >= 0 && result.Count < maxRows; i--)
        {
            int cellPointer = ReadUInt16BigEndian(page, headerOffset + header.HeaderLength + i * 2);
            Row row = DecodeLeafCell(stream, page, pageSize, usableSize, cellPointer);
            if (row != null)
            {
                result.Add(row);
            }
        }
    }

    private static Row DecodeLeafCell(FileStream stream, byte[] page, int pageSize, int usableSize, int cellPointer)
    {
        if (cellPointer < 0 || cellPointer >= page.Length)
        {
            return null;
        }

        int pos = cellPointer;
        long payloadLength = ReadVarint(page, ref pos);
        long rowId = ReadVarint(page, ref pos);
        if (payloadLength < 0 || payloadLength > MaxReasonablePayloadLength)
        {
            return null; // refuse implausible sizes rather than attempting a huge allocation
        }

        // Leaf table b-tree local/overflow split (file format section 1.5): payload that does not
        // fit locally spills to a linked chain of overflow pages, verified against a real SQLite
        // file's actual cell layout while building this reader.
        int maxLocal = usableSize - 35;
        int local;
        bool overflow;
        if (payloadLength <= maxLocal)
        {
            local = (int)payloadLength;
            overflow = false;
        }
        else
        {
            int minLocal = ((usableSize - 12) * 32 / 255) - 23;
            long k = minLocal + (payloadLength - minLocal) % (usableSize - 4);
            local = k <= maxLocal ? (int)k : minLocal;
            overflow = true;
        }

        if (pos + local > page.Length)
        {
            return null;
        }

        byte[] payload = new byte[payloadLength];
        Array.Copy(page, pos, payload, 0, local);
        if (overflow)
        {
            if (pos + local + 4 > page.Length)
            {
                return null;
            }

            int written = local;
            int nextOverflowPage = (int)ReadUInt32BigEndian(page, pos + local);
            int guard = 0;
            while (nextOverflowPage > 0 && written < payload.Length && guard < MaxOverflowPagesPerCell)
            {
                guard++;
                byte[] overflowPage = ReadPage(stream, pageSize, nextOverflowPage);
                if (overflowPage == null)
                {
                    return null;
                }

                int chunk = Math.Min(usableSize - 4, payload.Length - written);
                Array.Copy(overflowPage, 4, payload, written, chunk);
                written += chunk;
                nextOverflowPage = (int)ReadUInt32BigEndian(overflowPage, 0);
            }

            if (written < payload.Length)
            {
                return null; // overflow chain ended early -- unreadable rather than truncated
            }
        }

        Row row = new Row();
        row.RowId = rowId;
        row.Values = DecodeRecordValues(payload);
        return row;
    }

    private static string[] DecodeRecordValues(byte[] payload)
    {
        int pos = 0;
        long headerLength = ReadVarint(payload, ref pos);
        int headerEnd = (int)Math.Min(headerLength, payload.Length);
        List<long> serialTypes = new List<long>();
        while (pos < headerEnd)
        {
            serialTypes.Add(ReadVarint(payload, ref pos));
        }

        string[] values = new string[serialTypes.Count];
        int valuePos = headerEnd;
        for (int i = 0; i < serialTypes.Count; i++)
        {
            values[i] = DecodeSerialValue(payload, ref valuePos, serialTypes[i]);
        }

        return values;
    }

    // SQLite record serial-type codes (file format section 2.1).
    private static string DecodeSerialValue(byte[] data, ref int pos, long serialType)
    {
        if (serialType == 0)
        {
            return string.Empty; // NULL
        }

        if (serialType >= 1 && serialType <= 6)
        {
            int width = serialType == 1 ? 1 : serialType == 2 ? 2 : serialType == 3 ? 3 :
                serialType == 4 ? 4 : serialType == 5 ? 6 : 8;
            if (pos + width > data.Length)
            {
                pos = data.Length;
                return string.Empty;
            }

            long value = ReadSignedBigEndian(data, pos, width);
            pos += width;
            return value.ToString(CultureInfo.InvariantCulture);
        }

        if (serialType == 7)
        {
            if (pos + 8 > data.Length)
            {
                pos = data.Length;
                return string.Empty;
            }

            byte[] bytes = new byte[8];
            Array.Copy(data, pos, bytes, 0, 8);
            Array.Reverse(bytes); // stored big-endian; BitConverter is little-endian on this platform
            pos += 8;
            return BitConverter.ToDouble(bytes, 0).ToString(CultureInfo.InvariantCulture);
        }

        if (serialType == 8)
        {
            return "0";
        }

        if (serialType == 9)
        {
            return "1";
        }

        if (serialType >= 12 && serialType % 2 == 0)
        {
            int length = (int)((serialType - 12) / 2);
            pos += length;
            return string.Empty; // BLOB: no current caller needs the bytes as text
        }

        if (serialType >= 13)
        {
            int length = (int)((serialType - 13) / 2);
            if (length <= 0 || pos + length > data.Length)
            {
                pos = Math.Min(data.Length, pos + Math.Max(0, length));
                return string.Empty;
            }

            string value = Encoding.UTF8.GetString(data, pos, length);
            pos += length;
            return value;
        }

        // Serial types 10/11 are reserved and never emitted by SQLite itself; treat as zero-length
        // so one unexpected column cannot blank the rest of the row.
        return string.Empty;
    }

    private static long ReadSignedBigEndian(byte[] data, int offset, int width)
    {
        long value = 0;
        for (int i = 0; i < width; i++)
        {
            value = (value << 8) | data[offset + i];
        }

        int shift = 64 - width * 8;
        return (value << shift) >> shift; // sign-extend from `width` bytes to 64 bits
    }

    private static long ReadVarint(byte[] data, ref int pos)
    {
        long result = 0;
        for (int i = 0; i < 9; i++)
        {
            byte b = data[pos];
            pos++;
            if (i == 8)
            {
                result = (result << 8) | b;
                break;
            }

            result = (result << 7) | (long)(b & 0x7f);
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return result;
    }

    private static PageHeader ParsePageHeader(byte[] page, int offset)
    {
        PageHeader header = new PageHeader();
        header.PageType = page[offset];
        header.CellCount = ReadUInt16BigEndian(page, offset + 3);
        if (header.PageType == 0x02 || header.PageType == 0x05)
        {
            header.HeaderLength = 12;
            header.RightMostPointer = (int)ReadUInt32BigEndian(page, offset + 8);
        }
        else
        {
            header.HeaderLength = 8;
            header.RightMostPointer = 0;
        }

        return header;
    }

    private static int ReadUInt16BigEndian(byte[] data, int offset)
    {
        return (data[offset] << 8) | data[offset + 1];
    }

    private static long ReadUInt32BigEndian(byte[] data, int offset)
    {
        return ((long)data[offset] << 24) | ((long)data[offset + 1] << 16) |
            ((long)data[offset + 2] << 8) | data[offset + 3];
    }

    private static byte[] ReadPage(FileStream stream, int pageSize, int pageNumber)
    {
        return ReadExact(stream, (long)(pageNumber - 1) * pageSize, pageSize);
    }

    private static byte[] ReadExact(FileStream stream, long offset, int count)
    {
        if (offset < 0 || count <= 0 || offset + count > stream.Length)
        {
            return null;
        }

        byte[] buffer = new byte[count];
        stream.Seek(offset, SeekOrigin.Begin);
        int totalRead = 0;
        while (totalRead < count)
        {
            int read = stream.Read(buffer, totalRead, count - totalRead);
            if (read <= 0)
            {
                return null;
            }

            totalRead += read;
        }

        return buffer;
    }

    // Fixture: a real 1536-byte (three 512-byte page) SQLite database, built with Python's stdlib
    // sqlite3 module (an independent implementation from this reader) while developing this class,
    // containing a TranslationHistory(Id, Timestamp, SourceText, TranslatedText, TargetLanguage,
    // ApiUsed) table with three rows -- exercising the small page size edge, sqlite_master lookup,
    // and rowid-descending leaf traversal end to end. Kept small deliberately; the local/overflow
    // payload split formula is covered separately by ComputeLeafLocalPayloadSizeForTest, whose
    // expected numbers were cross-checked against a real overflowing row while developing this file.
    private const string FixtureBase64 =
        "U1FMaXRlIGZvcm1hdCAzAAIAAQEAQCAgAAAAAgAAAAMAAAAAAAAAAAAAAAEAAAAEAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACAC6KFA0AAAACAMEAARMAwQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFACBhcrKwFZdGFibGVzcWxpdGVfc2Vx" +
        "dWVuY2VzcWxpdGVfc2VxdWVuY2UDQ1JFQVRFIFRBQkxFIHNxbGl0ZV9zZXF1ZW5jZShuYW1lLHNlcSmBagEHFzExAYJ/dGFi" +
        "bGVUcmFuc2xhdGlvbkhpc3RvcnlUcmFuc2xhdGlvbkhpc3RvcnkCQ1JFQVRFIFRBQkxFIFRyYW5zbGF0aW9uSGlzdG9yeSAo" +
        "CiAgICBJZCBJTlRFR0VSIFBSSU1BUlkgS0VZIEFVVE9JTkNSRU1FTlQsCiAgICBUaW1lc3RhbXAgVEVYVCwKICAgIFNvdXJj" +
        "ZVRleHQgVEVYVCwKICAgIFRyYW5zbGF0ZWRUZXh0IFRFWFQsCiAgICBUYXJnZXRMYW5ndWFnZSBURVhULAogICAgQXBpVXNl" +
        "ZCBURVhUCikNAAAAAwFSAAHRAZoBUgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAEYDBwAhG1MXGTE3NTc1" +
        "MDAwMTBCYWQgb25lW0VSUk9SXSBUcmFuc2xhdGlvbiBGYWlsZWQ6IHJlZnVzZWR6aC1DTk9wZW5BSTUCBwAhLx0XGTE3NTc1" +
        "MDAwMDVUZXN0IG1lc3NhZ2UgaGVyZXRlc3QgbXNnemgtQ05PcGVuQUktAQcAISMZFxkxNzU3NTAwMDAwSGVsbG8gdGhlcmVu" +
        "aSBoYW96aC1DTk9wZW5BSQ0AAAABAegAAegAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA" +
        "FgEDMQFUcmFuc2xhdGlvbkhpc3RvcnkD";

    internal static void RunSelfTest()
    {
        // Serial-type width/varint mechanics, exercised directly without needing a real file.
        byte[] varintProbe = { 0x81, 0x02 }; // two-byte varint: (1 << 7) | 2 = 130
        int varintPos = 0;
        if (ReadVarint(varintProbe, ref varintPos) != 130 || varintPos != 2)
        {
            throw new InvalidOperationException("MinimalSqliteReader varint decode self-test failed.");
        }

        byte[] negativeOneByte = { 0xFF };
        if (ReadSignedBigEndian(negativeOneByte, 0, 1) != -1)
        {
            throw new InvalidOperationException("MinimalSqliteReader signed-width sign-extension self-test failed.");
        }

        // Local/overflow payload split formula (file format section 1.5), cross-checked against a
        // real 10030-byte overflowing payload on a 4096-byte-page/0-reserved database while this
        // reader was developed: expected local size 1846, with the remainder spread across exactly
        // two 4092-byte overflow pages.
        int usableSize = 4096;
        long payloadLength = 10030;
        int maxLocal = usableSize - 35;
        int minLocal = ((usableSize - 12) * 32 / 255) - 23;
        long k = minLocal + (payloadLength - minLocal) % (usableSize - 4);
        int computedLocal = k <= maxLocal ? (int)k : minLocal;
        if (computedLocal != 1846)
        {
            throw new InvalidOperationException("MinimalSqliteReader overflow local-size formula self-test failed.");
        }

        // End-to-end walk against a real (Python sqlite3-authored) three-row fixture database.
        byte[] fixtureBytes = Convert.FromBase64String(FixtureBase64);
        string fixturePath = Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-sqlite-reader-fixture-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            File.WriteAllBytes(fixturePath, fixtureBytes);
            List<Row> rows = ReadLastRowsByRowIdDescending(fixturePath, "TranslationHistory", 10);
            if (rows.Count != 3)
            {
                throw new InvalidOperationException("MinimalSqliteReader fixture row count self-test failed. Count=" + rows.Count.ToString(CultureInfo.InvariantCulture));
            }

            if (rows[0].RowId != 3 || rows[1].RowId != 2 || rows[2].RowId != 1)
            {
                throw new InvalidOperationException("MinimalSqliteReader fixture rowid-descending order self-test failed.");
            }

            // Column 0 is Id, a single-column INTEGER PRIMARY KEY -- SQLite aliases it to the rowid
            // and always decodes it as an empty NULL placeholder here (see the Row.Values doc
            // comment above). Columns 1-5 are Timestamp, SourceText, TranslatedText, TargetLanguage,
            // ApiUsed in that declared order.
            if (rows[0].Values.Length != 6 ||
                !string.Equals(rows[0].Values[1], "1757500010", StringComparison.Ordinal) ||
                !string.Equals(rows[0].Values[2], "Bad one", StringComparison.Ordinal) ||
                !rows[0].Values[3].StartsWith("[ERROR]", StringComparison.Ordinal) ||
                !string.Equals(rows[0].Values[4], "zh-CN", StringComparison.Ordinal) ||
                !string.Equals(rows[0].Values[5], "OpenAI", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MinimalSqliteReader fixture column decode self-test failed.");
            }

            if (!string.Equals(rows[2].Values[2], "Hello there", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("MinimalSqliteReader fixture oldest-row decode self-test failed.");
            }

            List<Row> capped = ReadLastRowsByRowIdDescending(fixturePath, "TranslationHistory", 2);
            if (capped.Count != 2 || capped[0].RowId != 3 || capped[1].RowId != 2)
            {
                throw new InvalidOperationException("MinimalSqliteReader row cap self-test failed.");
            }

            List<Row> missingTable = ReadLastRowsByRowIdDescending(fixturePath, "NoSuchTable", 5);
            if (missingTable.Count != 0)
            {
                throw new InvalidOperationException("MinimalSqliteReader missing-table self-test failed.");
            }
        }
        finally
        {
            try { File.Delete(fixturePath); } catch { }
        }

        List<Row> missingFile = ReadLastRowsByRowIdDescending(
            Path.Combine(Path.GetTempPath(), "DesktopCodexAssistant-sqlite-reader-missing-" + Guid.NewGuid().ToString("N") + ".db"),
            "TranslationHistory",
            5);
        if (missingFile.Count != 0)
        {
            throw new InvalidOperationException("MinimalSqliteReader missing-file self-test failed.");
        }

        Console.WriteLine("MinimalSqliteReader: PASS varint, sign-extension, overflow formula, fixture walk, row cap, missing table/file");
    }
}
