using System;
using System.Collections.Generic;
using System.Text;

namespace VisionAlgorithm.OpenSrc.IndustrialOcrOcv;

/// <summary>
/// User-injected OCV field specification combining a compiled character-class mask (used strictly as a candidate
/// template constraint, never to rewrite decoded characters) and an optional expected lot/date string.
/// Supports wildcard symbols (<c>D</c>, <c>A</c>, <c>a</c>, <c>X</c>, <c>#</c>), backslash literal escapes
/// (e.g. <c>\A</c>, <c>\D</c>, <c>\\</c>, <c>\[</c>), and character-class brackets (e.g. <c>[0-9A-F]</c>).
/// </summary>
public sealed record OcrFieldFormatSpec
{
    public const bool DefaultStrictLengthMatch = true;

    public const char DigitMaskSymbol = 'D';
    public const char UpperAlphaMaskSymbol = 'A';
    public const char LowerAlphaMaskSymbol = 'a';
    public const char AlphanumericMaskSymbol = 'X';
    public const char AnyGraphicMaskSymbol = '#';
    public const char EscapePrefixSymbol = '\\';
    public const char ClassOpenSymbol = '[';
    public const char ClassCloseSymbol = ']';

    private readonly MaskSlot[] _slots;

    public OcrFieldFormatSpec(
        string? FormatMask = null,
        string? ExpectedText = null,
        bool StrictLengthMatch = DefaultStrictLengthMatch)
    {
        this.FormatMask = FormatMask;
        this.ExpectedText = ExpectedText;
        this.StrictLengthMatch = StrictLengthMatch;
        CompactFormatMask = StripWhitespace(FormatMask);
        CompactExpectedText = StripWhitespace(ExpectedText);
        _slots = CompileMaskSlots(FormatMask);
    }

    public string? FormatMask { get; }

    public string? ExpectedText { get; }

    public bool StrictLengthMatch { get; }

    public string CompactFormatMask { get; }

    public string CompactExpectedText { get; }

    public int SlotCount => _slots.Length;

    public bool AllowsCharacterAt(int nonSpaceCharIndex, char candidate)
    {
        if (_slots.Length == 0)
        {
            return true;
        }

        if ((uint)nonSpaceCharIndex >= (uint)_slots.Length)
        {
            return !StrictLengthMatch;
        }

        return _slots[nonSpaceCharIndex].Matches(candidate);
    }

    public char? GetExpectedCharacterAt(int nonSpaceCharIndex)
    {
        if (string.IsNullOrEmpty(CompactExpectedText))
        {
            return null;
        }

        if ((uint)nonSpaceCharIndex >= (uint)CompactExpectedText.Length)
        {
            return null;
        }

        return CompactExpectedText[nonSpaceCharIndex];
    }

    private static MaskSlot[] CompileMaskSlots(string? mask)
    {
        if (string.IsNullOrWhiteSpace(mask))
        {
            return [];
        }

        var slots = new List<MaskSlot>(mask.Length);
        int i = 0;
        while (i < mask.Length)
        {
            char c = mask[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == EscapePrefixSymbol)
            {
                if (i + 1 >= mask.Length)
                {
                    throw new FormatException("Trailing escape backslash '\\' at end of OcrFieldFormatSpec.FormatMask.");
                }

                char literal = mask[i + 1];
                slots.Add(MaskSlot.Literal(literal));
                i += 2;
                continue;
            }

            if (c == ClassOpenSymbol)
            {
                slots.Add(ParseBracketClass(mask, ref i));
                continue;
            }

            if (c == ClassCloseSymbol)
            {
                throw new FormatException($"Unmatched closing bracket ']' at index {i} in OcrFieldFormatSpec.FormatMask.");
            }

            slots.Add(c switch
            {
                DigitMaskSymbol => MaskSlot.BuiltIn(SlotKind.Digit),
                UpperAlphaMaskSymbol => MaskSlot.BuiltIn(SlotKind.UpperAlpha),
                LowerAlphaMaskSymbol => MaskSlot.BuiltIn(SlotKind.LowerAlpha),
                AlphanumericMaskSymbol => MaskSlot.BuiltIn(SlotKind.Alphanumeric),
                AnyGraphicMaskSymbol => MaskSlot.BuiltIn(SlotKind.AnyGraphic),
                _ => MaskSlot.Literal(c),
            });
            i++;
        }

        return slots.ToArray();
    }

    private static MaskSlot ParseBracketClass(string mask, ref int index)
    {
        int openIndex = index;
        index++; // skip '['
        var ranges = new List<(char Low, char High)>(4);

        while (index < mask.Length && mask[index] != ClassCloseSymbol)
        {
            char startChar = ReadClassAtom(mask, ref index);
            if (index + 1 < mask.Length && mask[index] == '-' && mask[index + 1] != ClassCloseSymbol)
            {
                index++; // skip '-'
                char endChar = ReadClassAtom(mask, ref index);
                if (endChar < startChar)
                {
                    throw new FormatException(
                        $"Inverted character range '{startChar}-{endChar}' inside '[...]' starting at index {openIndex}.");
                }

                ranges.Add((startChar, endChar));
            }
            else
            {
                ranges.Add((startChar, startChar));
            }
        }

        if (index >= mask.Length || mask[index] != ClassCloseSymbol)
        {
            throw new FormatException($"Unclosed character class '[' starting at index {openIndex} in OcrFieldFormatSpec.FormatMask.");
        }

        if (ranges.Count == 0)
        {
            throw new FormatException($"Empty character class '[]' at index {openIndex} in OcrFieldFormatSpec.FormatMask.");
        }

        index++; // skip ']'
        return MaskSlot.CharacterClass(ranges.ToArray());
    }

    private static char ReadClassAtom(string mask, ref int index)
    {
        char c = mask[index];
        if (c == EscapePrefixSymbol)
        {
            if (index + 1 >= mask.Length)
            {
                throw new FormatException("Trailing escape backslash '\\' inside '[...]' in OcrFieldFormatSpec.FormatMask.");
            }

            char escaped = mask[index + 1];
            index += 2;
            return escaped;
        }

        index++;
        return c;
    }

    private static string StripWhitespace(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
            {
                sb.Append(value[i]);
            }
        }

        return sb.ToString();
    }

    private enum SlotKind : byte
    {
        Literal = 0,
        Digit = 1,
        UpperAlpha = 2,
        LowerAlpha = 3,
        Alphanumeric = 4,
        AnyGraphic = 5,
        CharacterClass = 6,
    }

    private readonly struct MaskSlot
    {
        private readonly SlotKind _kind;
        private readonly char _literal;
        private readonly (char Low, char High)[]? _ranges;

        private MaskSlot(SlotKind kind, char literal, (char Low, char High)[]? ranges)
        {
            _kind = kind;
            _literal = literal;
            _ranges = ranges;
        }

        public static MaskSlot BuiltIn(SlotKind kind) => new(kind, '\0', null);

        public static MaskSlot Literal(char literal) => new(SlotKind.Literal, literal, null);

        public static MaskSlot CharacterClass((char Low, char High)[] ranges) => new(SlotKind.CharacterClass, '\0', ranges);

        public bool Matches(char candidate)
        {
            switch (_kind)
            {
                case SlotKind.Literal:
                    return candidate == _literal;
                case SlotKind.Digit:
                    return candidate is >= '0' and <= '9';
                case SlotKind.UpperAlpha:
                    return candidate is >= 'A' and <= 'Z';
                case SlotKind.LowerAlpha:
                    return candidate is >= 'a' and <= 'z';
                case SlotKind.Alphanumeric:
                    return (candidate is >= '0' and <= '9') ||
                           (candidate is >= 'A' and <= 'Z') ||
                           (candidate is >= 'a' and <= 'z');
                case SlotKind.AnyGraphic:
                    return !char.IsWhiteSpace(candidate);
                case SlotKind.CharacterClass:
                    if (_ranges is null)
                    {
                        return false;
                    }

                    for (int i = 0; i < _ranges.Length; i++)
                    {
                        (char low, char high) = _ranges[i];
                        if (candidate >= low && candidate <= high)
                        {
                            return true;
                        }
                    }

                    return false;
                default:
                    return false;
            }
        }
    }
}
