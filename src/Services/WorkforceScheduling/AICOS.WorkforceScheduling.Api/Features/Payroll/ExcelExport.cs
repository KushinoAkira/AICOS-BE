using AICOS.WorkforceScheduling.Api.Domain;
using ClosedXML.Excel;

namespace AICOS.WorkforceScheduling.Api.Features.Payroll;

/// <summary>
/// Excel (.xlsx) versions of the payroll exports (SCRUM-129). Text is always written as text, never as a formula, so a hostile employee
/// name such as "=HYPERLINK(...)" cannot run when the file is opened. Numbers are real numbers, so the sheet can be summed and filtered.
/// </summary>
public static class ExcelExport
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static byte[] Payroll(PayrollSummaryResponse summary)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Payroll");
        string[] headers =
        [
            "Employee", "Contract", "Worked days", "Regular hours", "Overtime hours", "Replacement hours", "Absences", "Unreviewed records",
            "Base pay", "Overtime pay", "Replacement pay", "Adjustments", "Total pay",
        ];
        Header(sheet, headers);
        sheet.Column(1).Width = 28;

        var row = 2;
        foreach (var l in summary.Lines)
        {
            Text(sheet, row, 1, l.EmployeeName);
            Text(sheet, row, 2, l.ContractType.ToString());
            Number(sheet, row, 3, l.WorkedDays);
            Number(sheet, row, 4, (decimal)l.RegularHours);
            Number(sheet, row, 5, (decimal)l.OvertimeHours);
            Number(sheet, row, 6, (decimal)l.ReplacementHours);
            Number(sheet, row, 7, l.Absences);
            Number(sheet, row, 8, l.UnreviewedRecords);
            Money(sheet, row, 9, l.BasePay);
            Money(sheet, row, 10, l.OvertimePay);
            Money(sheet, row, 11, l.ReplacementPay);
            Money(sheet, row, 12, l.Adjustments);
            Money(sheet, row, 13, l.TotalPay);
            row++;
        }

        Text(sheet, row, 1, "TOTAL");
        Number(sheet, row, 8, summary.Totals.UnreviewedRecords);
        Money(sheet, row, 9, summary.Totals.BasePay);
        Money(sheet, row, 10, summary.Totals.OvertimePay);
        Money(sheet, row, 11, summary.Totals.ReplacementPay);
        Money(sheet, row, 12, summary.Totals.Adjustments);
        Money(sheet, row, 13, summary.Totals.TotalPay);
        sheet.Row(row).Style.Font.Bold = true;

        sheet.SheetView.FreezeRows(1);
        Widths(sheet);
        return Save(workbook);
    }

    public sealed record RecordRow(
        DateOnly Date, string Employee, string ShiftStart, string ShiftEnd, AssignmentStatus Assignment, bool Replacement, string CheckIn, string CheckOut,
        double WorkedHours, bool Absent, AttendanceReviewStatus Review, bool CountsForPayroll);

    public static byte[] Records(IReadOnlyList<RecordRow> rows)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Work records");
        Header(sheet, ["Date", "Employee", "Shift start", "Shift end", "Assignment", "Replacement", "Check-in", "Check-out", "Worked hours", "Absent", "Review", "Counts for payroll"]);

        var row = 2;
        foreach (var r in rows)
        {
            Text(sheet, row, 1, r.Date.ToString("yyyy-MM-dd"));
            Text(sheet, row, 2, r.Employee);
            Text(sheet, row, 3, r.ShiftStart);
            Text(sheet, row, 4, r.ShiftEnd);
            Text(sheet, row, 5, r.Assignment.ToString());
            Text(sheet, row, 6, r.Replacement ? "yes" : "no");
            Text(sheet, row, 7, r.CheckIn);
            Text(sheet, row, 8, r.CheckOut);
            Number(sheet, row, 9, (decimal)r.WorkedHours);
            Text(sheet, row, 10, r.Absent ? "yes" : "no");
            Text(sheet, row, 11, r.Review.ToString());
            Text(sheet, row, 12, r.CountsForPayroll ? "yes" : "no");
            row++;
        }

        sheet.SheetView.FreezeRows(1);
        Widths(sheet);
        return Save(workbook);
    }

    private static void Header(IXLWorksheet sheet, string[] headers)
    {
        for (var i = 0; i < headers.Length; i++)
        {
            var cell = sheet.Cell(1, i + 1);
            cell.SetValue(headers[i]);
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
        }
    }

    /// <summary>Fixed widths: measuring text needs fonts, which a slim Linux container does not have.</summary>
    private static void Widths(IXLWorksheet sheet)
    {
        sheet.Columns().Width = 16;
        sheet.Column(2).Width = 22;
    }

    private static void Text(IXLWorksheet sheet, int row, int column, string value) => sheet.Cell(row, column).SetValue(value);

    private static void Number(IXLWorksheet sheet, int row, int column, decimal value) => sheet.Cell(row, column).SetValue(value);

    private static void Number(IXLWorksheet sheet, int row, int column, int value) => sheet.Cell(row, column).SetValue(value);

    private static void Money(IXLWorksheet sheet, int row, int column, decimal value)
    {
        var cell = sheet.Cell(row, column);
        cell.SetValue(value);
        cell.Style.NumberFormat.Format = "#,##0";
    }

    private static byte[] Save(XLWorkbook workbook)
    {
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }
}
