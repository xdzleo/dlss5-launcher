using System.Diagnostics;
using RenoDXLauncher.Services;

var sw = Stopwatch.StartNew();
var steam = StoreScanners.ScanSteam();
Console.WriteLine($"Steam:     {steam.Count,3}  ({sw.ElapsedMilliseconds} ms)");
sw.Restart();
Console.WriteLine($"Epic:      {StoreScanners.ScanEpic().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"GOG:       {StoreScanners.ScanGog().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"Xbox:      {StoreScanners.ScanXbox().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"Ubisoft:   {StoreScanners.ScanUbisoft().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"EA:        {StoreScanners.ScanEa().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"BattleNet: {StoreScanners.ScanBattleNet().Count,3}  ({sw.ElapsedMilliseconds} ms)"); sw.Restart();
Console.WriteLine($"Rockstar:  {StoreScanners.ScanRockstar().Count,3}  ({sw.ElapsedMilliseconds} ms)");

var catalog = await new CatalogService().LoadAsync();
var known = catalog.SelectMany(e => e.NormalizedAliases).ToHashSet(StringComparer.Ordinal);
bool Known(string n) => known.Contains(MatchService.Normalize(n))
    || known.Contains(MatchService.Normalize(MatchService.StripEditionSuffix(n)));
sw.Restart();
var folders = StoreScanners.ScanGameFolders(Known);
Console.WriteLine($"Pastas:    {folders.Count,3}  ({sw.ElapsedMilliseconds} ms)");

sw.Restart();
var all = await StoreScanners.ScanAllAsync(Known);
Console.WriteLine($"\nTOTAL apos dedupe: {all.Count}  ({sw.ElapsedMilliseconds} ms)");
var mods = all.Count(g => MatchService.FindMatch(g, catalog) != null);
Console.WriteLine($"com mod: {mods}");
Console.WriteLine("\npor loja:");
foreach (var g in all.GroupBy(x => x.Store).OrderByDescending(x => x.Count()))
    Console.WriteLine($"  {g.Key,-10} {g.Count()}");
