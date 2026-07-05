namespace EDetection.Desktop.Models;

public sealed class DetectionLogItem
{
    public DetectionLogItem(string kind, string message)
    {
        Kind = kind;
        Message = message;
        TimeText = DateTime.Now.ToString("HH:mm:ss");
    }

    public string TimeText { get; }

    public string Kind { get; }

    public string Message { get; }

    public bool Matches(string query, string kind)
    {
        if (!string.IsNullOrWhiteSpace(kind)
            && !string.Equals(Kind, kind, StringComparison.CurrentCultureIgnoreCase))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        return string.Join('\n', TimeText, Kind, Message)
            .Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    public string ToTsv() =>
        string.Join(
            "\t",
            TimeText,
            Kind,
            Message.ReplaceLineEndings(" "));
}
