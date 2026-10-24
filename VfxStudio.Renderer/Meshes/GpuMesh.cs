using System.Numerics;
using System.Runtime.InteropServices;
using ReeLib;
using ReeLib.Mesh;
using Silk.NET.OpenGL;

namespace VfxStudio.Renderer.Meshes;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct GpuVertex
{
    public Vector3 Position;
    public Vector3 Normal;
    public Vector2 TexCoords;
    public uint Bone0;
    public uint Bone1;
    public uint Bone2;
    public uint Bone3;
    public float Weight0;
    public float Weight1;
    public float Weight2;
    public float Weight3;
}

public class GpuSubmesh
{
    public int IndexOffset { get; set; }
    public int IndexCount { get; set; }
    public int MaterialIndex { get; set; }
    public string MaterialName { get; set; } = string.Empty;
}

public class GpuMesh : IDisposable
{
    private readonly GL _gl;
    public uint Vao { get; private set; }
    public uint Vbo { get; private set; }
    public uint Ebo { get; private set; }

    public int TotalVertices { get; private set; }
    public int TotalIndices { get; private set; }
    public Vector3 BoundsMin { get; private set; }
    public Vector3 BoundsMax { get; private set; }
    public List<GpuSubmesh> Submeshes { get; } = new();

    public GpuMesh(GL gl)
    {
        _gl = gl;
    }

    public static GpuMesh CreateFromMeshFile(GL gl, MeshFile mesh)
    {
        var gpuMesh = new GpuMesh(gl);

        var vertices = new List<GpuVertex>();
        var indices = new List<uint>();

        if (mesh.MeshData?.LODs.Count > 0)
        {
            // Select LOD 0 if it has valid vertices, or find the LOD with vertices populated
            MeshLOD? targetLod = null;
            for (int l = 0; l < mesh.MeshData.LODs.Count; l++)
            {
                var lod = mesh.MeshData.LODs[l];
                if (lod.MeshGroups.Any(g => g.Submeshes.Any(s => s.Buffer.Positions.Length >= s.vertsIndexOffset + Math.Min(1, s.vertCount) && s.vertCount > 0)))
                {
                    targetLod = lod;
                    break;
                }
            }
            targetLod ??= mesh.MeshData.LODs[0];

            foreach (var group in targetLod.MeshGroups)
            {
                foreach (var sub in group.Submeshes)
                {
                    var buf = sub.Buffer;
                    if (buf.Positions.Length == 0 || sub.vertCount <= 0 || sub.indicesCount <= 0) continue;

                    int startIndexInIndices = indices.Count;
                    uint submeshBaseVertex = (uint)vertices.Count;

                    // Ensure submesh vertex range does not exceed buffer
                    int actualVertCount = Math.Min(sub.vertCount, buf.Positions.Length - sub.vertsIndexOffset);
                    if (actualVertCount <= 0) continue;

                    // Extract vertices for this submesh
                    for (int i = 0; i < actualVertCount; i++)
                    {
                        int vIdx = sub.vertsIndexOffset + i;
                        var pos = buf.Positions[vIdx];
                        var norm = (vIdx < buf.NormalsTangents.Length) ? buf.NormalsTangents[vIdx].Normal : Vector3.UnitY;
                        var uv = (vIdx < buf.UV0.Length) ? buf.UV0[vIdx].AsVector2 : Vector2.Zero;

                        // Rigid weapon vertices: Bone weights 0 to prevent hunter bone distortion
                        vertices.Add(new GpuVertex
                        {
                            Position = pos,
                            Normal = norm,
                            TexCoords = uv,
                            Bone0 = 0,
                            Bone1 = 0,
                            Bone2 = 0,
                            Bone3 = 0,
                            Weight0 = 0f,
                            Weight1 = 0f,
                            Weight2 = 0f,
                            Weight3 = 0f
                        });
                    }

                    // Extract indices in triplets (triangles) to prevent broken topology & desync spikes
                    bool hasShortFaces = buf.Faces != null;
                    bool hasIntFaces = buf.IntegerFaces != null;
                    int maxFaces = hasShortFaces ? buf.Faces!.Length : (hasIntFaces ? buf.IntegerFaces!.Length : 0);

                    for (int i = 0; i + 2 < sub.indicesCount; i += 3)
                    {
                        int f0 = sub.facesIndexOffset + i;
                        int f1 = sub.facesIndexOffset + i + 1;
                        int f2 = sub.facesIndexOffset + i + 2;

                        if (f2 >= maxFaces) break;

                        uint r0 = hasShortFaces ? buf.Faces![f0] : (uint)buf.IntegerFaces![f0];
                        uint r1 = hasShortFaces ? buf.Faces![f1] : (uint)buf.IntegerFaces![f1];
                        uint r2 = hasShortFaces ? buf.Faces![f2] : (uint)buf.IntegerFaces![f2];

                        // Validate that all 3 indices fall within the extracted vertex count of this submesh
                        if (r0 < actualVertCount && r1 < actualVertCount && r2 < actualVertCount)
                        {
                            indices.Add(submeshBaseVertex + r0);
                            indices.Add(submeshBaseVertex + r1);
                            indices.Add(submeshBaseVertex + r2);
                        }
                    }

                    string matName = (sub.materialIndex < mesh.MaterialNames.Count) ? mesh.MaterialNames[sub.materialIndex] : $"Material_{sub.materialIndex}";
                    gpuMesh.Submeshes.Add(new GpuSubmesh
                    {
                        IndexOffset = startIndexInIndices,
                        IndexCount = indices.Count - startIndexInIndices,
                        MaterialIndex = sub.materialIndex,
                        MaterialName = matName
                    });
                }
            }
        }

        return CreateFromRawBuffers(gl, vertices, indices, gpuMesh.Submeshes);
    }

    public static GpuMesh CreateFromRawBuffers(GL gl, List<GpuVertex> vertices, List<uint> indices, List<GpuSubmesh> submeshes)
    {
        var gpuMesh = new GpuMesh(gl);
        gpuMesh.Submeshes.AddRange(submeshes);
        gpuMesh.TotalVertices = vertices.Count;
        gpuMesh.TotalIndices = indices.Count;

        if (vertices.Count == 0 || indices.Count == 0)
        {
            return gpuMesh;
        }

        Vector3 min = new(float.PositiveInfinity);
        Vector3 max = new(float.NegativeInfinity);
        foreach (var vertex in vertices)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }
        gpuMesh.BoundsMin = min;
        gpuMesh.BoundsMax = max;

        // Upload to GPU
        gpuMesh.Vao = gl.GenVertexArray();
        gpuMesh.Vbo = gl.GenBuffer();
        gpuMesh.Ebo = gl.GenBuffer();

        gl.BindVertexArray(gpuMesh.Vao);

        unsafe
        {
            var vertSpan = CollectionsMarshal.AsSpan(vertices);
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, gpuMesh.Vbo);
            fixed (GpuVertex* ptr = vertSpan)
            {
                gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(vertSpan.Length * sizeof(GpuVertex)), ptr, BufferUsageARB.StaticDraw);
            }

            var idxSpan = CollectionsMarshal.AsSpan(indices);
            gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, gpuMesh.Ebo);
            fixed (uint* ptr = idxSpan)
            {
                gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(idxSpan.Length * sizeof(uint)), ptr, BufferUsageARB.StaticDraw);
            }

            uint stride = (uint)sizeof(GpuVertex);

            // 0: Position (vec3)
            gl.EnableVertexAttribArray(0);
            gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, (void*)0);

            // 1: Normal (vec3)
            gl.EnableVertexAttribArray(1);
            gl.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, (void*)12);

            // 2: TexCoords (vec2)
            gl.EnableVertexAttribArray(2);
            gl.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, (void*)24);

            // 3: BoneIndices (uvec4)
            gl.EnableVertexAttribArray(3);
            gl.VertexAttribIPointer(3, 4, VertexAttribIType.UnsignedInt, stride, (void*)32);

            // 4: BoneWeights (vec4)
            gl.EnableVertexAttribArray(4);
            gl.VertexAttribPointer(4, 4, VertexAttribPointerType.Float, false, stride, (void*)48);
        }

        gl.BindVertexArray(0);
        return gpuMesh;
    }

    public void Render()
    {
        if (Vao == 0 || TotalIndices == 0) return;

        _gl.BindVertexArray(Vao);
        unsafe
        {
            _gl.DrawElements(PrimitiveType.Triangles, (uint)TotalIndices, DrawElementsType.UnsignedInt, (void*)0);
        }
        _gl.BindVertexArray(0);
    }

    public void Draw() => Render();

    public void RenderSubmesh(int submeshIndex)
    {
        if (Vao == 0 || submeshIndex < 0 || submeshIndex >= Submeshes.Count) return;

        var sub = Submeshes[submeshIndex];
        if (sub.IndexCount == 0) return;

        _gl.BindVertexArray(Vao);
        unsafe
        {
            void* offset = (void*)(sub.IndexOffset * sizeof(uint));
            _gl.DrawElements(PrimitiveType.Triangles, (uint)sub.IndexCount, DrawElementsType.UnsignedInt, offset);
        }
        _gl.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (Vao != 0)
        {
            _gl.DeleteVertexArray(Vao);
            Vao = 0;
        }
        if (Vbo != 0)
        {
            _gl.DeleteBuffer(Vbo);
            Vbo = 0;
        }
        if (Ebo != 0)
        {
            _gl.DeleteBuffer(Ebo);
            Ebo = 0;
        }
    }
}
