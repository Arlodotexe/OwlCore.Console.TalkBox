public static class PathRangeHelpers
{
    public static (Range? LineRange, Range? ColumnRange) GetRanges(string rangesGiven)
    {
        // Following range formats can be specified:
        // --- S = Start, E = End ---
        // --- N = Entire line ---
        // LSCS-LECE
        // LS-LE
        // LN
        var rangesSplit = rangesGiven.Split('-');

        // LN formatting
        if (rangesSplit.Length < 2)
        {
            var lineGiven = int.Parse(rangesGiven.ToUpperInvariant().Replace("L", ""));
            return (new Range(lineGiven - 1, lineGiven), null);
        }

        // LSCS-LECE or LS-LE formatting
        var start = rangesSplit[0];
        var end = rangesSplit[1];

        // Detect if start columns
        // LSCS formatting
        var columnStartSplit = start.Split('C');
        var hasStartColumns = columnStartSplit.Length > 1;
        int? startColumn = hasStartColumns ? int.Parse(columnStartSplit[1].ToString()) - 1 : null;

        // Detect if end columns
        // LECE formatting
        var columnEndSplit = end.Split('C');
        var hasEndColumns = columnEndSplit.Length > 1;
        int? endColumn = hasEndColumns ? int.Parse(columnEndSplit[1].ToString()) : null;

        if (hasEndColumns && !hasStartColumns)
        {
            throw new InvalidOperationException("End column was included but start column was not.");
        }

        var hasColumns = hasStartColumns && hasEndColumns;

        // Detect start lines
        // LSCS formatting
        var startLineSplit = columnStartSplit[0].Split('L');
        var startLineStr = startLineSplit[1];
        var startLine = int.Parse($"{startLineStr}") - 1;

        // Detect end lines
        // LECE formatting
        var endLineSplit = columnEndSplit[0].Split('L');
        var endLineStr = endLineSplit[1];
        var endLine = int.Parse($"{endLineStr}");

        if (hasColumns)
        {
            if (startColumn is null || endColumn is null)
                throw new InvalidOperationException();

            return (new Range(startLine, endLine), new Range(startColumn.Value, endColumn.Value));
        }

        return (new Range(startLine, endLine), null);
    }
}