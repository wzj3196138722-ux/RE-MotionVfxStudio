using System.Text.Json;
using ReeLib; using ReeLib.Efx;
// Read-only: lists root entries of corpus files (file offset of each entry header, its attribute types).
var root = args[0]; var outp = args[1]; int N = int.Parse(args[2]);
var rows = new List<object>();
foreach (var p in Directory.EnumerateFiles(root, "*.efx.*", SearchOption.AllDirectories).Take(N)) {
  try {
    var efx = new EfxFile(new FileHandler(new MemoryStream(File.ReadAllBytes(p), writable:false), p));
    efx.Read();
    foreach (var e in efx.Entries)
      rows.Add(new { path = p, start = e.Start, count = e.Attributes.Count,
                     types = e.Attributes.Select(a => a.type.ToString()).ToArray(),
                     sizes = e.Attributes.Select(a => a.ExpectedByteSize).ToArray() });
  } catch { }
}
File.WriteAllText(outp, JsonSerializer.Serialize(rows));
Console.WriteLine($"entries {rows.Count}");
