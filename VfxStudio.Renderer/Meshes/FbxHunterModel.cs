using System.Numerics;
using Assimp;
using ReeLib.Common;
using Silk.NET.OpenGL;
using VfxStudio.Core.Models;
using VfxStudio.Renderer.Animation;
using Matrix4x4 = System.Numerics.Matrix4x4;

namespace VfxStudio.Renderer.Meshes;

public class HunterBone
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public uint Hash { get; set; }
    public int ParentIndex { get; set; } = -1;
    public Matrix4x4 LocalRestTransform { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 InverseBindPose { get; set; } = Matrix4x4.Identity;
    public Matrix4x4 GlobalRestTransform { get; set; } = Matrix4x4.Identity;
}

public class FbxHunterModel : IDisposable
{
    private readonly GL _gl;
    public GpuMesh Mesh { get; private set; }
    public List<HunterBone> Bones { get; } = new();
    public Dictionary<string, int> BoneNameToIndex { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<uint, int> BoneHashToIndex { get; } = new();

    public FbxHunterModel(GL gl, GpuMesh mesh)
    {
        _gl = gl;
        Mesh = mesh;
    }

    public static FbxHunterModel Load(GL gl, string fbxPath)
    {
        if (!File.Exists(fbxPath))
        {
            throw new FileNotFoundException($"FBX Hunter model file not found: {fbxPath}");
        }

        using var importer = new AssimpContext();
        var scene = importer.ImportFile(
            fbxPath,
            PostProcessSteps.Triangulate |
            PostProcessSteps.GenerateNormals |
            PostProcessSteps.LimitBoneWeights
        );

        if (scene == null || !scene.HasMeshes)
        {
            throw new InvalidOperationException($"Failed to load FBX model from {fbxPath}");
        }

        // 1. Gather all bone offset matrices from all meshes
        var boneOffsetMap = new Dictionary<string, Matrix4x4>(StringComparer.OrdinalIgnoreCase);
        foreach (var mesh in scene.Meshes)
        {
            foreach (var b in mesh.Bones)
            {
                if (!boneOffsetMap.ContainsKey(b.Name))
                {
                    boneOffsetMap[b.Name] = Matrix4x4.Transpose(b.OffsetMatrix);
                }
            }
        }

        // 2. Discover and build skeleton tree in topological parent-before-child order
        var bones = new List<HunterBone>();
        var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // Find "root" node inside Armature
        var rootNode = scene.RootNode.FindNode("root") ?? scene.RootNode;

        void TraverseNode(Node node, int parentIdx, Matrix4x4 parentGlobal)
        {
            if (bones.Count >= GpuConfig.MaxBones) return;

            string name = node.Name;
            int currentIdx = -1;

            // Only add bone nodes (exclude camera/light/root wrapper nodes)
            if (!string.IsNullOrEmpty(name) && !name.Equals("RootNode", StringComparison.OrdinalIgnoreCase) && !name.Contains("Armature", StringComparison.OrdinalIgnoreCase))
            {
                currentIdx = bones.Count;
                var localTransform = Matrix4x4.Transpose(node.Transform);
                var globalRest = localTransform * parentGlobal;

                // Lookup InverseBindPose from mesh skinning or invert global rest
                Matrix4x4 invBind;
                if (boneOffsetMap.TryGetValue(name, out var offsetMat))
                {
                    invBind = offsetMat;
                }
                else
                {
                    Matrix4x4.Invert(globalRest, out invBind);
                }

                uint hash = MurMur3HashUtils.GetHash(name);

                var bone = new HunterBone
                {
                    Index = currentIdx,
                    Name = name,
                    Hash = hash,
                    ParentIndex = parentIdx,
                    LocalRestTransform = localTransform,
                    InverseBindPose = invBind,
                    GlobalRestTransform = globalRest
                };

                bones.Add(bone);
                nameToIndex[name] = currentIdx;
                parentGlobal = globalRest;
                parentIdx = currentIdx;
            }

            foreach (var child in node.Children)
            {
                TraverseNode(child, parentIdx, parentGlobal);
            }
        }

        TraverseNode(rootNode, -1, Matrix4x4.Identity);

        // Also add any mesh bones that were not visited in rootNode tree (if any)
        foreach (var (bName, offsetMat) in boneOffsetMap)
        {
            if (!nameToIndex.ContainsKey(bName) && bones.Count < GpuConfig.MaxBones)
            {
                int idx = bones.Count;
                uint hash = MurMur3HashUtils.GetHash(bName);
                bones.Add(new HunterBone
                {
                    Index = idx,
                    Name = bName,
                    Hash = hash,
                    ParentIndex = -1,
                    LocalRestTransform = Matrix4x4.Identity,
                    InverseBindPose = offsetMat,
                    GlobalRestTransform = Matrix4x4.Identity
                });
                nameToIndex[bName] = idx;
            }
        }

        // 3. Build unified GpuMesh with all submeshes
        var allVertices = new List<GpuVertex>();
        var allIndices = new List<uint>();
        var submeshes = new List<GpuSubmesh>();

        for (int m = 0; m < scene.MeshCount; m++)
        {
            var mesh = scene.Meshes[m];
            if (mesh.VertexCount == 0) continue;

            int baseVertex = allVertices.Count;
            int startIndex = allIndices.Count;

            // Pre-map vertex bone weights
            var vertBones = new (uint boneIdx, float weight)[mesh.VertexCount, 4];
            var vertBoneCounts = new int[mesh.VertexCount];

            foreach (var b in mesh.Bones)
            {
                if (!nameToIndex.TryGetValue(b.Name, out int boneIdx))
                    continue;

                foreach (var vw in b.VertexWeights)
                {
                    int vId = vw.VertexID;
                    if (vId >= mesh.VertexCount) continue;

                    int slot = vertBoneCounts[vId];
                    if (slot < 4)
                    {
                        vertBones[vId, slot] = ((uint)boneIdx, vw.Weight);
                        vertBoneCounts[vId]++;
                    }
                }
            }

            // Extract vertices
            for (int v = 0; v < mesh.VertexCount; v++)
            {
                var pos = new Vector3(mesh.Vertices[v].X, mesh.Vertices[v].Y, mesh.Vertices[v].Z);
                var norm = mesh.HasNormals
                    ? new Vector3(mesh.Normals[v].X, mesh.Normals[v].Y, mesh.Normals[v].Z)
                    : Vector3.UnitY;
                var uv = mesh.HasTextureCoords(0)
                    ? new Vector2(mesh.TextureCoordinateChannels[0][v].X, mesh.TextureCoordinateChannels[0][v].Y)
                    : Vector2.Zero;

                uint b0 = vertBones[v, 0].boneIdx;
                uint b1 = vertBones[v, 1].boneIdx;
                uint b2 = vertBones[v, 2].boneIdx;
                uint b3 = vertBones[v, 3].boneIdx;

                float w0 = vertBones[v, 0].weight;
                float w1 = vertBones[v, 1].weight;
                float w2 = vertBones[v, 2].weight;
                float w3 = vertBones[v, 3].weight;

                float totalW = w0 + w1 + w2 + w3;
                if (totalW > 0.0001f)
                {
                    w0 /= totalW;
                    w1 /= totalW;
                    w2 /= totalW;
                    w3 /= totalW;
                }
                else
                {
                    // Fallback to COG or root
                    b0 = (uint)(nameToIndex.TryGetValue("COG", out int cogIdx) ? cogIdx : 0);
                    w0 = 1.0f;
                }

                allVertices.Add(new GpuVertex
                {
                    Position = pos,
                    Normal = norm,
                    TexCoords = uv,
                    Bone0 = b0,
                    Bone1 = b1,
                    Bone2 = b2,
                    Bone3 = b3,
                    Weight0 = w0,
                    Weight1 = w1,
                    Weight2 = w2,
                    Weight3 = w3
                });
            }

            // Extract face indices
            foreach (var face in mesh.Faces)
            {
                if (face.IndexCount == 3)
                {
                    allIndices.Add((uint)(baseVertex + face.Indices[0]));
                    allIndices.Add((uint)(baseVertex + face.Indices[1]));
                    allIndices.Add((uint)(baseVertex + face.Indices[2]));
                }
            }

            string matName = (mesh.MaterialIndex < scene.Materials.Count)
                ? scene.Materials[mesh.MaterialIndex].Name
                : $"Material_{m}";

            submeshes.Add(new GpuSubmesh
            {
                IndexOffset = startIndex,
                IndexCount = allIndices.Count - startIndex,
                MaterialIndex = mesh.MaterialIndex,
                MaterialName = matName
            });
        }

        // Upload combined vertices and indices to GPU
        var gpuMesh = GpuMesh.CreateFromRawBuffers(gl, allVertices, allIndices, submeshes);

        var model = new FbxHunterModel(gl, gpuMesh);
        model.Bones.AddRange(bones);
        foreach (var (k, v) in nameToIndex)
        {
            model.BoneNameToIndex[k] = v;
        }
        foreach (var b in bones)
        {
            model.BoneHashToIndex[b.Hash] = b.Index;
        }

        return model;
    }

    public void Dispose()
    {
        Mesh?.Dispose();
    }
}
