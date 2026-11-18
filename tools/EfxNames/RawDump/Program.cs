using System.Text.Json;
using ReeLib; using ReeLib.Efx;
// Read-only: each corpus file is read through a non-writable MemoryStream; attributes are serialized into
// a fresh in-memory FileHandler (no path), never to disk. Output: per type, up to N distinct payloads (hex).
var root = args[0]; var outp = args[1]; var types = new HashSet<string>(args[2].Split(',')); int N = int.Parse(args[3]);
var res = new Dictionary<string, Dictionary<string,int>>();
int files = 0, err = 0, sizeMismatch = 0;
foreach (var p in Directory.EnumerateFiles(root, "*.efx.*", SearchOption.AllDirectories)) {
  files++;
  try {
    var efx = new EfxFile(new FileHandler(new MemoryStream(File.ReadAllBytes(p), writable:false), p));
    efx.Read();
    foreach (var a in efx.GetAttributesAndActions(true)) {
      var k = a.type.ToString(); if (!types.Contains(k)) continue;
      var ms = new MemoryStream(); var h = new FileHandler(ms);
      a.Write(h);
      var b = ms.ToArray();
      if (a.ExpectedByteSize >= 0 && b.Length != a.ExpectedByteSize - 4) sizeMismatch++;
      if (!res.TryGetValue(k, out var d)) res[k] = d = new();
      var hx = Convert.ToHexString(b);
      if (d.ContainsKey(hx)) d[hx]++; else if (d.Count < N) d[hx] = 1;
    }
  } catch { err++; }
}
File.WriteAllText(outp, JsonSerializer.Serialize(res));
Console.WriteLine($"files {files} err {err} sizeMismatch {sizeMismatch} " + string.Join(" ", res.Select(kv => kv.Key + ":" + kv.Value.Count)));
