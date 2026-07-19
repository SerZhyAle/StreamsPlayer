using System.Text;

namespace StreamsPlayer.Core;

public static class Rfc4180Csv
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string text)
    {
        var rows = new List<IReadOnlyList<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quoted)
            {
                if (character == '"')
                {
                    if (index + 1 < text.Length && text[index + 1] == '"')
                    {
                        field.Append('"');
                        index++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    field.Append(character);
                }

                continue;
            }

            switch (character)
            {
                case '"' when field.Length == 0:
                    quoted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    break;
                case '\r':
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }
                    CompleteRow();
                    break;
                case '\n':
                    CompleteRow();
                    break;
                default:
                    field.Append(character);
                    break;
            }
        }

        if (quoted)
        {
            throw new FormatException("The CSV ends inside a quoted field.");
        }

        if (field.Length > 0 || row.Count > 0)
        {
            CompleteRow();
        }

        return rows;

        void CompleteRow()
        {
            row.Add(field.ToString());
            field.Clear();
            rows.Add(row.ToArray());
            row.Clear();
        }
    }
}
