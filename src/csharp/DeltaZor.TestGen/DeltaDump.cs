namespace DZ.TestGen;

using System;
using System.Numerics;
using System.Text;

public static class DeltaDump
{
    private const string Yellow = "<span style=\"color: #E5C07B;\">"; // Header output_length (warm yellow for dark bg)
    private const string Magenta = "<span style=\"color: #C678DD;\">"; // Compression type (soft purple)
    private const string Red = "<span style=\"color: #E06C75;\">"; // Opcodes (muted red)
    private const string Blue = "<span style=\"color: #61AFEF;\">"; // Parameters (bright blue)
    private const string Green = "<span style=\"color: #98C379;\">"; // Data runs (fresh green)
    private const string Cyan = "<span style=\"color: #56B6C2;\">"; // Checksum (cool cyan)
    private const string Reset = "</span>";

    public static string ColoredDeltaDump(ReadOnlySpan<byte> data, bool hasChecksum = true)
    {
        if (data.IsEmpty) return string.Empty;

        string[] byteColors = new string[data.Length];
        for (int i = 0; i < byteColors.Length; i++)
        {
            byteColors[i] = string.Empty; // Default no color
        }

        int end = hasChecksum ? data.Length - 4 : data.Length;
        int pos = 0;

        // Header: output_length (4 bytes)
        if (pos + 4 <= end)
        {
            for (int i = 0; i < 4; i++)
            {
                byteColors[pos + i] = Yellow;
            }
            pos += 4;
        }

        // Compression type (1 byte)
        if (pos < end)
        {
            byteColors[pos] = Magenta;
            byte compressionType = data[pos];
            pos++;

            if (compressionType == 0x01) // Full Replace
            {
                // Data until end
                for (int i = pos; i < end; i++)
                {
                    byteColors[i] = Green;
                }
            }
            else if (compressionType == 0x00) // RLE Delta
            {
                while (pos < end)
                {
                    if (pos >= end) break;
                    byteColors[pos] = Red;
                    byte opcode = data[pos];
                    pos++;

                    switch (opcode)
                    {
                        case 0x00: // Zero Run: [opcode][count:7bit]
                        case 0x02: // Extension: [opcode][count:7bit][data:count]
                        case 0x01: // Non-Zero Run: [opcode][count:7bit][data:count]
                            {
                                int varintStart = pos;
                                if (!TryRead7BitEncodedInt(data, ref pos, out int count))
                                    break; // Invalid, leave uncolored
                                for (int i = varintStart; i < pos; i++)
                                {
                                    byteColors[i] = Blue;
                                }
                                if (opcode != 0x00) // Has data
                                {
                                    for (int i = 0; i < count && pos + i < end; i++)
                                    {
                                        byteColors[pos + i] = Green;
                                    }
                                    pos += count;
                                }
                            }
                            break;

                        case 0x03: // Truncation: [opcode][new_length:4]
                            {
                                if (pos + 4 <= end)
                                {
                                    for (int i = 0; i < 4; i++)
                                    {
                                        byteColors[pos + i] = Blue;
                                    }
                                    pos += 4;
                                }
                            }
                            break;

                        case 0x04: // Uniform Motif Repeat
                        case 0x05: // Varying Motif Repeat
                            {
                                if (pos >= end) break;
                                byteColors[pos] = Blue; // Flags
                                byte flags = data[pos];
                                pos++;

                                int repeatStart = pos;
                                if (!TryRead7BitEncodedInt(data, ref pos, out int repeatLength))
                                    break;
                                for (int i = repeatStart; i < pos; i++)
                                {
                                    byteColors[i] = Blue;
                                }

                                int unitStart = pos;
                                if (!TryRead7BitEncodedInt(data, ref pos, out int unitSize))
                                    break;
                                for (int i = unitStart; i < pos; i++)
                                {
                                    byteColors[i] = Blue;
                                }

                                bool isMasked = (flags & 0x80) != 0;
                                uint mask = 0;
                                int changedCount = unitSize;
                                if (isMasked)
                                {
                                    int maskStart = pos;
                                    if (!TryRead7BitEncodedInt(data, ref pos, out int maskInt))
                                        break;
                                    mask = (uint)maskInt;
                                    for (int i = maskStart; i < pos; i++)
                                    {
                                    byteColors[i] = Blue;
                                    }
                                    changedCount = BitOperations.PopCount(mask);
                                }

                                int dataSize = (opcode == 0x04) ? changedCount : changedCount * repeatLength;
                                for (int i = 0; i < dataSize && pos + i < end; i++)
                                {
                                    byteColors[pos + i] = Green;
                                }
                                pos += dataSize;
                            }
                            break;

                        default:
                            // Unknown opcode, leave uncolored or handle pending
                            break;
                    }
                }
            }
        }

        // Checksum (last 4 bytes if enabled)
        if (hasChecksum && data.Length >= 4)
        {
            int checksumStart = data.Length - 4;
            for (int i = checksumStart; i < data.Length; i++)
            {
                byteColors[i] = Cyan;
            }
        }

        // Build the legend
        var sb = new StringBuilder();
        sb.AppendLine("**Legend:**");
        sb.AppendLine("- " + Yellow + "Header (output_length)" + Reset);
        sb.AppendLine("- " + Magenta + "Compression type" + Reset);
        sb.AppendLine("- " + Red + "Opcodes" + Reset);
        sb.AppendLine("- " + Blue + "Parameters (varints, flags, masks)" + Reset);
        sb.AppendLine("- " + Green + "Data runs (XOR, extension, motif data)" + Reset);
        sb.AppendLine("- " + Cyan + "Checksum" + Reset);
        sb.AppendLine();

        // Build the hex dump with HTML spans
        for (int i = 0; i < data.Length; i += 16)
        {
            sb.Append($"{i:x4}: ");
            for (int j = 0; j < 16; j++)
            {
                if (i + j < data.Length)
                {
                    string color = byteColors[i + j];
                    if (!string.IsNullOrEmpty(color))
                    {
                        sb.Append(color);
                    }
                    sb.Append($"{data[i + j]:x2}");
                    if (!string.IsNullOrEmpty(color))
                    {
                        sb.Append(Reset);
                    }
                    sb.Append(' ');
                }
                else
                {
                    sb.Append("   ");
                }
            }
            sb.AppendLine();
        }
        return sb.ToString().TrimEnd();
    }

    private static bool TryRead7BitEncodedInt(ReadOnlySpan<byte> span, ref int position, out int value)
    {
        value = 0;
        int shift = 0;
        byte b;
        int start = position;

        do
        {
            if (position >= span.Length)
                return false;
            b = span[position++];
            value |= (b & 0x7F) << shift;
            shift += 7;
            if (shift > 35)
                return false;
        } while ((b & 0x80) != 0);

        return true;
    }
}
