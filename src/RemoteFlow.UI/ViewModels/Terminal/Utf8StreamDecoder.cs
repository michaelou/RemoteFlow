using System.Buffers;
using System.Text;

namespace RemoteFlow.UI.ViewModels.Terminal;

internal sealed class Utf8StreamDecoder
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();

    public string Decode(in ReadOnlySequence<byte> bytes, bool flush = false)
    {
        var builder = new StringBuilder();
        foreach (var segment in bytes)
        {
            DecodeSegment(segment.Span, flush: false, builder);
        }

        if (flush)
        {
            DecodeSegment([], flush: true, builder);
        }

        return builder.ToString();
    }

    private void DecodeSegment(ReadOnlySpan<byte> bytes, bool flush, StringBuilder builder)
    {
        var maximumChars = Math.Max(Encoding.UTF8.GetMaxCharCount(bytes.Length), 2);
        var characters = ArrayPool<char>.Shared.Rent(maximumChars);
        try
        {
            do
            {
                _decoder.Convert(
                    bytes,
                    characters.AsSpan(0, maximumChars),
                    flush,
                    out var bytesUsed,
                    out var charsUsed,
                    out var completed);
                if (charsUsed > 0)
                {
                    _ = builder.Append(characters, 0, charsUsed);
                }

                bytes = bytes[bytesUsed..];
                if (completed)
                {
                    break;
                }
            }
            while (!bytes.IsEmpty || flush);
        }
        finally
        {
            ArrayPool<char>.Shared.Return(characters, clearArray: true);
        }
    }
}
