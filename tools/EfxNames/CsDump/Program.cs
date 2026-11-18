using System.Text.Json;
using ReeLib; using ReeLib.Efx;
var res = new Dictionary<string, object>();
foreach (var ver in new[]{EfxVersion.RE4, EfxVersion.MHWilds}) {
  var list = new List<object>();
  foreach (var (id, kind) in EfxAttributeTypeRemapper.GetAllTypes(ver).OrderBy(p=>p.Key)) {
    string cls=null; int size=-1; string err=null; List<string[]> fields=new();
    try {
      var a = EFXAttribute.Create(ver, kind);
      cls = a.GetType().Name;
      foreach (var (n,t) in EfxTools.GetFieldInfo(a.GetType(), ver)) fields.Add(new[]{n, t.Name});
      using var ms = new MemoryStream(); var h = new FileHandler(ms);
      try { a.Write(h); size=(int)ms.Length; } catch(Exception e){ err="write:"+e.GetType().Name; }
    } catch(Exception e) { err=e.GetType().Name; }
    list.Add(new { id, kind=kind.ToString(), cls, size, err, fields });
  }
  res[ver.ToString()] = list;
}
File.WriteAllText(args[0], JsonSerializer.Serialize(res, new JsonSerializerOptions{WriteIndented=false}));
Console.WriteLine("ok");
