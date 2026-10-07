// Checks the ops dashboard (workbook.json) before it reaches Azure, as a .NET 10 single-file app:
//
//   dotnet run check-workbook.cs -- workbook.json
//
// Azure accepts a workbook whatever its queries say, and a broken one only shows up as an error on a
// chart. So every query is parsed and checked against the Application Insights tables and columns it
// uses, with Microsoft's own Kusto parser, and every chart must point at a resource main.bicep fills in.
#:package Microsoft.Azure.Kusto.Language@12.2.0
#:property PublishAot=false

using System.Text.Json;
using Kusto.Language;
using Kusto.Language.Symbols;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run check-workbook.cs -- <workbook.json>");
    return 2;
}

// The parts of the Application Insights tables the dashboard reads.
const string Common = "timestamp:datetime, name:string, user_AuthenticatedId:string, user_Id:string, client_Type:string, " +
                      "application_Version:string, cloud_RoleName:string, operation_Id:string, customDimensions:dynamic";
TableSymbol Table(string name, string columns) => TableSymbol.From($"({Common}, {columns})").WithName(name);
var appInsights = GlobalState.Default.WithDatabase(new DatabaseSymbol("appinsights",
    Table("requests", "url:string, resultCode:string, success:bool, duration:real"),
    Table("pageViews", "url:string, duration:real"),
    Table("customMetrics", "value:real, valueSum:real, valueCount:int"),
    Table("exceptions", "type:string, outerMessage:string, problemId:string"),
    Table("browserTimings", "totalDuration:real, networkDuration:real")));

// The placeholders main.bicep replaces with resource IDs.
string[] placeholders = ["__APP_INSIGHTS_ID__", "__API_ID__", "__DATABASE_ID__"];

var workbook = JsonDocument.Parse(File.ReadAllText(args[0])).RootElement;
var problems = new List<string>();
var names = new HashSet<string>();
foreach (var item in workbook.GetProperty("items").EnumerateArray())
{
    var name = item.GetProperty("name").GetString()!;
    if (!names.Add(name)) problems.Add($"{name}: two items have this name");
    var content = item.GetProperty("content");

    switch (item.GetProperty("type").GetInt32())
    {
        case 3: // a query
            if (content.GetProperty("crossComponentResources").EnumerateArray().Select(r => r.GetString()).SingleOrDefault() != "__APP_INSIGHTS_ID__")
                problems.Add($"{name}: must query __APP_INSIGHTS_ID__");
            if (content.GetProperty("timeContextFromParameter").GetString() != "TimeRange")
                problems.Add($"{name}: must follow the TimeRange picker");
            var query = content.GetProperty("query").GetString()!.Replace("{TimeRange:grain}", "1h");
            foreach (var error in KustoCode.ParseAndAnalyze(query, appInsights).GetDiagnostics().Where(d => d.Severity == "Error"))
                problems.Add($"{name}: {error.Message} (at \"{query.Substring(error.Start, Math.Min(error.Length, 40))}\")");
            break;
        case 10: // Azure Monitor metrics
            foreach (var resource in content.GetProperty("resourceIds").EnumerateArray().Select(r => r.GetString()))
                if (!placeholders.Contains(resource)) problems.Add($"{name}: unknown resource {resource}");
            break;
    }
}

foreach (var problem in problems) Console.Error.WriteLine(problem);
Console.WriteLine(problems.Count == 0 ? $"All {names.Count} items in {args[0]} check out." : $"{problems.Count} problem(s).");
return problems.Count == 0 ? 0 : 1;
