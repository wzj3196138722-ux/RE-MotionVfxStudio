using System.Collections.ObjectModel;
using ReeLib.Common;
using ReeLib.Pfb;
using ReeLib.via;

namespace ReeLib.Pfb
{
    public class PfbHeaderStruct : BaseModel
    {
        public uint magic;
        public int infoCount;
        public int resourceCount;
        public int gameObjectRefInfoCount;
        public long userdataCount;
        public long gameObjectRefInfoOffset;
        public long resourceInfoOffset;
        public long userdataInfoOffset;
        public long dataOffset;

        protected override bool DoRead(FileHandler handler)
        {
            handler.Read(ref magic);
            handler.Read(ref infoCount);
            handler.Read(ref resourceCount);
            handler.Read(ref gameObjectRefInfoCount);
            if (handler.FileVersion > 16) handler.Read(ref userdataCount);
            handler.Read(ref gameObjectRefInfoOffset);
            handler.Read(ref resourceInfoOffset);
            if (handler.FileVersion > 16) handler.Read(ref userdataInfoOffset);
            handler.Read(ref dataOffset);
            return true;
        }

        protected override bool DoWrite(FileHandler handler)
        {
            handler.Write(ref magic);
            handler.Write(ref infoCount);
            handler.Write(ref resourceCount);
            handler.Write(ref gameObjectRefInfoCount);
            if (handler.FileVersion > 16) handler.Write(ref userdataCount);
            handler.Write(ref gameObjectRefInfoOffset);
            handler.Write(ref resourceInfoOffset);
            if (handler.FileVersion > 16) handler.Write(ref userdataInfoOffset);
            handler.Write(ref dataOffset);
            return true;
        }
    }

    public struct PfbGameObjectInfo
    {
        public int objectId;
        public int parentId;
        public int componentCount;
    }

    public struct PfbGameObjectRefInfo
    {
        public int objectId;
        public int propertyId;
        public int arrayIndex;
        public int targetId;

        public override string ToString() => $"<{objectId}>.{propertyId}[{arrayIndex}] = {targetId}";
    }

    public class PfbGameObject : IGameObject
    {
        private WeakReference<PfbGameObject>? parentRef;
        public PfbGameObjectInfo Info;
        public ObservableCollection<RszInstance> Components { get; private set; } = new();
        public ObservableCollection<PfbGameObject> Children { get; private set; } = new();
        public RszInstance? Instance { get; set; }

        IList<RszInstance> IGameObject.Components => Components;

        public PfbGameObject? Parent
        {
            get => parentRef != null && parentRef.TryGetTarget(out var target) ? target : null;
            set => parentRef = value != null ? new(value) : null;
        }

        public string? Name => Instance?.Values.Length > 0 ? Instance.Values[0] as string : null;

        public int? ObjectId => Info.objectId;

        public string? GetHierarchyPath()
        {
            string? path = Name;
            var nextChild = this;
            while (nextChild.Parent != null && nextChild.Parent != this)
            {
                path = $"{nextChild.Parent.Name}.{path}";
                nextChild = nextChild.Parent;
            }
            return path;
        }

        public PfbGameObject Clone()
        {
            PfbGameObject gameObject = new()
            {
                Info = Info,
                Components = new(Components.Select(item => item.CloneCached())),
                Instance = Instance?.CloneCached(),
            };
            foreach (var child in Children)
            {
                var newChild = child.Clone();
                newChild.Parent = gameObject;
                gameObject.Children.Add(newChild);
            }
            return gameObject;
        }

        public override string ToString() => Name ?? "";

        public IEnumerable<IGameObject> GetChildren() => Children;
    }
}

namespace ReeLib
{
    public class PfbFile : BaseRszFile
    {
        public PfbHeaderStruct Header { get; } = new();
        public List<PfbGameObjectInfo> GameObjectInfoList { get; } = new();
        public List<PfbGameObjectRefInfo> GameObjectRefInfoList { get; } = new();
        public List<ResourceInfo> ResourceInfoList { get; } = new();
        public List<UserdataInfo> UserdataInfoList { get; } = new();
        public RSZFile RSZ { get; }
        public ObservableCollection<PfbGameObject> GameObjects { get; } = new();
        public bool ResourceChanged { get; set; } = false;
        public PfbGameObject? Root => GameObjects.FirstOrDefault();

        public PfbFile(RszFileOption option, FileHandler fileHandler) : base(option, fileHandler)
        {
            RSZ = new RSZFile(option, fileHandler);
        }

        public const uint Magic = 0x424650;

        public override RSZFile? GetRSZ() => RSZ;

        protected override bool DoRead()
        {
            GameObjectInfoList.Clear();
            GameObjectRefInfoList.Clear();
            ResourceInfoList.Clear();
            UserdataInfoList.Clear();
            GameObjects?.Clear();

            var handler = FileHandler;
            var header = Header;
            if (!header.Read(handler)) return false;
            if (header.magic != Magic)
            {
                throw new InvalidDataException($"{handler.FilePath} Not a PFB file");
            }

            GameObjectInfoList.ReadStructList(handler, header.infoCount);

            handler.Seek(header.gameObjectRefInfoOffset);
            GameObjectRefInfoList.ReadStructList(handler, header.gameObjectRefInfoCount);

            handler.Seek(header.resourceInfoOffset);
            for (int i = 0; i < header.resourceCount; i++)
            {
                ResourceInfo item = new(Option.Version, true);
                if (!item.Read(handler)) return false;
                ResourceInfoList.Add(item);
            }

            handler.Seek(header.userdataInfoOffset);
            if (handler.FileVersion > 16)
            {
                UserdataInfoList.Read(handler, (int)header.userdataCount);
            }

            ReadRsz(RSZ, header.dataOffset);
            return true;
        }

        protected override bool DoWrite()
        {
            if (StructChanged)
            {
                RebuildInfoTables();
            }
            else if (ResourceChanged)
            {
                RszUtils.SyncResourceFromRsz(ResourceInfoList, RSZ);
                ResourceChanged = false;
            }
            FileHandler handler = FileHandler;
            handler.Clear();
            var header = Header;
            header.Write(handler);
            GameObjectInfoList.Write(handler);

            if (GameObjectRefInfoList.Count > 0)
            {
                header.gameObjectRefInfoOffset = handler.Tell();
                GameObjectRefInfoList.Write(handler);
            }

            handler.Align(16);
            header.resourceInfoOffset = handler.Tell();
            foreach (var item in ResourceInfoList)
            {
                if (handler.FileVersion <= 16) item.HasOffset = false;
                if (!item.Write(handler)) return false;
            }

            if (handler.FileVersion > 16 && UserdataInfoList.Count > 0)
            {
                handler.Align(16);
                header.userdataInfoOffset = handler.Tell();
                UserdataInfoList.Write(handler);
            }

            handler.StringTableFlush();

            handler.Align(16);
            header.dataOffset = handler.Tell();
            WriteRsz(RSZ, header.dataOffset);

            header.magic = Magic;
            header.infoCount = GameObjectInfoList.Count;
            header.resourceCount = ResourceInfoList.Count;
            header.gameObjectRefInfoCount = GameObjectRefInfoList.Count;
            header.userdataCount = UserdataInfoList.Count;
            header.Write(handler, 0);

            return true;
        }

        public void SetupGameObjects()
        {
            Dictionary<int, PfbGameObject> gameObjectMap = new();
            foreach (var info in GameObjectInfoList)
            {
                PfbGameObject gameObjectData = new()
                {
                    Info = info,
                    Instance = RSZ.ObjectList[info.objectId],
                };
                for (int i = 0; i < info.componentCount; i++)
                {
                    gameObjectData.Components.Add(RSZ.ObjectList[info.objectId + 1 + i]);
                }
                gameObjectMap[info.objectId] = gameObjectData;
                if (info.parentId == -1)
                {
                    GameObjects.Add(gameObjectData);
                }
            }

            foreach (var info in GameObjectInfoList)
            {
                var gameObject = gameObjectMap[info.objectId];
                if (gameObjectMap.TryGetValue(info.parentId, out var parent))
                {
                    parent.Children.Add(gameObject);
                    gameObject.Parent = parent;
                }
            }
        }

        public static void CollectGameObjectInstances(PfbGameObject gameObject, List<RszInstance> rszInstances)
        {
            rszInstances.Add(gameObject.Instance!);
            foreach (var item in gameObject.Components)
            {
                rszInstances.Add(item);
            }
            foreach (var child in gameObject.Children)
            {
                CollectGameObjectInstances(child, rszInstances);
            }
        }

        public void RebuildInfoTables()
        {
            List<RszInstance> rszInstances = new();
            if (GameObjects != null)
            {
                foreach (var gameObjectData in GameObjects)
                {
                    CollectGameObjectInstances(gameObjectData, rszInstances);
                }
            }

            RSZ.RebuildInstanceList(rszInstances);
            RSZ.RebuildInstanceInfo(false, false);
            RSZ.ClearObjects();

            GameObjectInfoList.Clear();
            if (GameObjects != null)
            {
                foreach (var gameObjectData in GameObjects)
                {
                    AddGameObjectInfoRecursion(gameObjectData);
                }
            }

            RszUtils.SyncUserDataFromRsz(UserdataInfoList, RSZ);
            RszUtils.SyncResourceFromRsz(ResourceInfoList, RSZ);
            StructChanged = false;
            ResourceChanged = false;
        }

        private void AddGameObjectInfoRecursion(PfbGameObject gameObject)
        {
            var instance = gameObject.Instance!;
            if (instance.ObjectTableIndex != -1) return;
            ref var infoData = ref gameObject.Info;
            RSZ.AddToObjectTable(instance);
            infoData.objectId = instance.ObjectTableIndex;
            infoData.parentId = gameObject.Parent?.ObjectId ?? -1;
            infoData.componentCount = (short)gameObject.Components.Count;

            GameObjectInfoList.Add(gameObject.Info);
            foreach (var item in gameObject.Components)
            {
                RSZ.AddToObjectTable(item);
            }
            foreach (var child in gameObject.Children)
            {
                AddGameObjectInfoRecursion(child);
            }
        }
    }
}
