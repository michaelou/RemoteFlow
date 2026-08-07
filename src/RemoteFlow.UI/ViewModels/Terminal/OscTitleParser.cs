using System.Text;

namespace RemoteFlow.UI.ViewModels.Terminal;

internal sealed class OscTitleParser
{
    private readonly StringBuilder _sequence = new();
    private bool _inSequence;
    private bool _sawEscape;

    public IReadOnlyList<string> Process(string text)
    {
        var titles = new List<string>();
        foreach (var character in text)
        {
            if (!_inSequence)
            {
                if (_sawEscape && character == ']')
                {
                    _ = _sequence.Clear();
                    _inSequence = true;
                    _sawEscape = false;
                    continue;
                }

                _sawEscape = character == '\u001b';
                continue;
            }

            if (character == '\a' || (_sawEscape && character == '\\'))
            {
                AddTitle(titles);
                _ = _sequence.Clear();
                _inSequence = false;
                _sawEscape = false;
                continue;
            }

            if (_sawEscape)
            {
                _ = _sequence.Append('\u001b');
                _sawEscape = false;
            }

            if (character == '\u001b')
            {
                _sawEscape = true;
            }
            else if (_sequence.Length < 4096)
            {
                _ = _sequence.Append(character);
            }
            else
            {
                _ = _sequence.Clear();
                _inSequence = false;
            }
        }

        return titles;
    }

    private void AddTitle(List<string> titles)
    {
        var value = _sequence.ToString();
        var separator = value.IndexOf(';');
        if (separator < 1 || !int.TryParse(value.AsSpan(0, separator), out var command) || command is not (0 or 2))
        {
            return;
        }

        var title = value[(separator + 1)..].Trim();
        if (title.Length > 0)
        {
            titles.Add(title);
        }
    }
}
