namespace PrintFins;

static class Status
{
    public static void Line(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        foreach (var raw in message.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (raw.Length == 0)
                continue;
            try { Console.WriteLine(raw); } catch { }
        }
    }
}
