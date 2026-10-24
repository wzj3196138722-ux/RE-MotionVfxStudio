using System;
using System.IO;
using System.Text;

namespace MotionVfx.CacheDiagnostics
{
    /// <summary>A structural candidate check, never proof of a live resource's identity or provenance.</summary>
    public sealed class EfxBufferValidation
    {
        public bool Valid { get; internal set; }
        public int ExactLength { get; internal set; }
        public string Error { get; internal set; } = "";
        public int EntryCount { get; internal set; }
        public int ActionCount { get; internal set; }
        public int AttributeCount { get; internal set; }
    }

    /// <summary>
    /// Bounded framing validator for the serialized MHWilds EFX format (.efx.5571972).
    /// It examines offset zero only; it neither searches for magic nor reads process memory.
    /// A valid result identifies a structurally framed candidate, not a verified cached file.
    /// Attribute bodies remain opaque and must subsequently pass the complete EFX parser.
    ///
    /// The 48-byte header contains counts and string/group table lengths, NOT a file length
    /// or format-version field. The offset-12 value called "entryLength" by old templates is
    /// only the string table byte length. Names and resource paths are inline, not native pointers.
    /// Actions have 12-byte headers, entries have 16-byte headers. Every Wilds attribute has
    /// type:int32, size:int32, UniqueID:int32, payload; size INCLUDES the four-byte UniqueID.
    /// The two UVAR type words at the end are mandatory even when both are zero.
    /// Source: Core Formats/EfxFile.cs and REE-Lib.Generators/ReeLibGenerator.cs.
    /// </summary>
    public static class EfxBufferValidator
    {
        public const int WildsVersion = 5571972;
        public const int DefaultMaximumBytes = 16 * 1024 * 1024;
        private const int MaximumCount = 65536;
        private const int MaximumAttributes = 200000;
        private const int MaximumStringBytes = 8192;
        private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private static readonly Encoding StrictUtf16 = new UnicodeEncoding(false, false, true);

        public static EfxBufferValidation Validate(byte[] bytes, int maximumBytes = DefaultMaximumBytes)
        {
            if (bytes == null) return new EfxBufferValidation { Error = "No candidate bytes were supplied." };
            if (maximumBytes < 56 || maximumBytes > 256 * 1024 * 1024)
                return new EfxBufferValidation { Error = "The candidate bound must be between 56 bytes and 256 MiB." };
            try { return new Scanner(bytes, Math.Min(bytes.Length, maximumBytes)).Read(); }
            catch (Exception ex) when (ex is InvalidDataException || ex is OverflowException || ex is DecoderFallbackException)
            {
                return new EfxBufferValidation { Error = ex.Message };
            }
        }

        public static int Measure(byte[] bytes, int maximumBytes = DefaultMaximumBytes)
        {
            var result = Validate(bytes, maximumBytes);
            if (!result.Valid) throw new InvalidDataException(result.Error);
            return result.ExactLength;
        }

        private sealed class Scanner
        {
            private readonly byte[] bytes;
            private readonly int bound;
            private int position;
            private int attributeCount;

            public Scanner(byte[] bytes, int bound) { this.bytes = bytes; this.bound = bound; }

            private void Require(long count)
            {
                if (count < 0 || count > (long)bound - position)
                    throw new InvalidDataException($"Truncated candidate or size bound exceeded at {position}: need {count} bytes, available {bound - position}.");
            }

            private void Skip(long count) { Require(count); position = checked(position + (int)count); }

            private int Int32()
            {
                Require(4);
                // Explicit little endian, independent of the diagnostic host's byte order.
                int value = bytes[position] | bytes[position + 1] << 8 | bytes[position + 2] << 16 | bytes[position + 3] << 24;
                position += 4;
                return value;
            }

            private int Count(string label)
            {
                int value = Int32();
                if (value < 0 || value > MaximumCount)
                    throw new InvalidDataException($"Invalid {label} count {value} at {position - 4} (limit {MaximumCount}).");
                return value;
            }

            private int ByteLength(string label)
            {
                int value = Int32();
                if (value < 0 || value > bound)
                    throw new InvalidDataException($"Invalid {label} byte length {value} at {position - 4}.");
                return value;
            }

            private void ZeroTerminated(int unit, int regionEnd, int maximumBytes = MaximumStringBytes)
            {
                int start = position;
                int end = (int)Math.Min((long)regionEnd, (long)start + maximumBytes);
                for (; position <= end - unit; position += unit)
                {
                    Require(unit);
                    if (bytes[position] != 0 || (unit == 2 && bytes[position + 1] != 0)) continue;
                    (unit == 1 ? StrictUtf8 : StrictUtf16).GetCharCount(bytes, start, position - start);
                    position += unit;
                    return;
                }
                throw new InvalidDataException($"Unterminated or overlong {(unit == 1 ? "UTF-8" : "UTF-16")} string at {start}.");
            }

            private void BytePrefixedUtf16()
            {
                int count = ByteLength("UVAR string");
                if (count < 2 || count > MaximumStringBytes || (count & 1) != 0)
                    throw new InvalidDataException($"Invalid UVAR UTF-16 byte length {count}.");
                Require(count);
                int end = checked(position + count);
                ZeroTerminated(2, end, count);
                if (position != end)
                    throw new InvalidDataException("UVAR string has an early terminator or inconsistent declared length.");
            }

            private void Attributes(int count)
            {
                Require(checked(count * 12L)); // Type, size, UniqueID are mandatory for every attribute.
                attributeCount = checked(attributeCount + count);
                if (attributeCount > MaximumAttributes) throw new InvalidDataException("Attribute count exceeds the diagnostic limit.");
                for (int i = 0; i < count; i++)
                {
                    int type = Int32();
                    int size = Int32();
                    // The complete parser determines whether a positive type ID is implemented.
                    if (type < 0 || type > 4096 || size < 4)
                        throw new InvalidDataException($"Invalid attribute type/size {type}/{size} at {position - 8}.");
                    Skip(size); // Includes UniqueID; adding another four bytes would be incorrect.
                }
            }

            public EfxBufferValidation Read()
            {
                Require(48);
                if (Int32() != 0x72786665) throw new InvalidDataException("Candidate does not begin with serialized EFX magic (efxr).");
                int dimension = Int32();
                if (dimension < 0 || dimension > 1) throw new InvalidDataException("Invalid EFX dimension.");
                int entries = Count("entry");
                int stringBytes = ByteLength("string table");
                int actions = Count("action");
                int fields = Count("field parameter");
                int expressions = Count("expression parameter");
                int groups = Count("effect group");
                int groupBytes = ByteLength("effect group table");
                int bones = Count("bone");
                int relations = Count("bone relation");
                Count("property binding index"); // No independent data table in the Wilds serializer.

                Require(stringBytes);
                int stringEnd = checked(position + stringBytes);
                long minimumStrings = checked((expressions + (long)bones) * 3 + actions + fields + entries + groups);
                if (minimumStrings > stringBytes) throw new InvalidDataException("String table cannot contain the declared number of names.");
                for (int i = 0; i < expressions + bones; i++)
                {
                    ZeroTerminated(1, stringEnd);
                    ZeroTerminated(2, stringEnd);
                }
                for (int i = 0; i < actions + fields + entries + groups; i++) ZeroTerminated(1, stringEnd);
                if (position != stringEnd) throw new InvalidDataException("String table byte length does not match its names.");

                Skip(checked(expressions * 24L + bones * 8L + relations * 2L));
                for (int i = 0; i < actions; i++)
                {
                    Skip(8); // Action field and name hash.
                    Attributes(Count("action attribute"));
                }

                for (int i = 0; i < fields; i++)
                {
                    Skip(12);
                    int type = Int32();
                    Skip(4);
                    int firstValue = Int32();
                    if (type == 196)
                    {
                        // This special field stores a UTF-16 character-count bound in value_ukn1;
                        // the Core writer uses path.Length + 2 rather than the usual + 1.
                        if (firstValue < 1 || firstValue > MaximumStringBytes / 2)
                            throw new InvalidDataException("Invalid type-196 field string bound.");
                        ZeroTerminated(2, bound, checked(firstValue * 2));
                    }
                    else
                    {
                        Skip(24); // Remaining values including Wilds' extra float: 48-byte base.
                        if (type == 110 || type == 144 || type == 183 || type == 184 || type == 202 || type == 194 || type == 215 || type == 217)
                        {
                            int characters = Count("field path character");
                            if (characters < 1 || characters > MaximumStringBytes / 2)
                                throw new InvalidDataException("Invalid field path character bound.");
                            ZeroTerminated(2, bound, checked(characters * 2));
                        }
                    }
                }

                for (int i = 0; i < entries; i++)
                {
                    Skip(8); // Serialized entry ID and name hash, not a list position and file offset.
                    int assignment = Int32();
                    if (assignment < 0 || assignment > 2) throw new InvalidDataException("Invalid entry assignment.");
                    Attributes(Count("entry attribute"));
                }

                int groupStart = position;
                for (int i = 0; i < groups; i++)
                {
                    Skip(8);
                    int members = Count("group member");
                    Require(checked(members * 4L));
                    for (int j = 0; j < members; j++)
                    {
                        int entryIndex = Int32();
                        if (entryIndex < 0 || entryIndex >= entries) throw new InvalidDataException("Effect group references an invalid entry position.");
                    }
                }
                if (position - groupStart != groupBytes) throw new InvalidDataException("Effect group byte length is inconsistent.");

                int firstUvar = Int32();
                int secondUvar = Int32();
                for (int i = 0; i < 2; i++)
                {
                    int type = i == 0 ? firstUvar : secondUvar;
                    if (type < 0 || type > 2) throw new InvalidDataException("Unsupported UVAR type.");
                    if (type == 2) { BytePrefixedUtf16(); BytePrefixedUtf16(); }
                }
                return new EfxBufferValidation
                {
                    Valid = true, ExactLength = position, EntryCount = entries,
                    ActionCount = actions, AttributeCount = attributeCount
                };
            }
        }
    }
}
