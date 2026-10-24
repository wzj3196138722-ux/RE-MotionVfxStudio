"""Run with Blender --background --factory-startup --python capture_reference.py -- PLUGIN EFX OUTPUT.

Uses the reference add-on's actual import, simulation, and resource paths.  It
records render items before trying to compare pixels with the native app.
"""
import json
import os
import sys
from collections import Counter

import bpy
import gpu
import numpy as np
from mathutils import Matrix
from gpu_extras.batch import batch_for_shader

arguments = sys.argv[sys.argv.index("--") + 1:]
plugin_root, efx_path, output_path = arguments[:3]
image_path = arguments[3] if len(arguments) > 3 else None
plugin_root = os.path.abspath(plugin_root)
efx_path = os.path.abspath(efx_path)
output_path = os.path.abspath(output_path)
if image_path:
    image_path = os.path.abspath(image_path)
sys.path.insert(0, plugin_root)
import blender_efx_re
from blender_efx_re import bridge, io_tree, sim_preview

blender_efx_re.register()
scene = bpy.context.scene
data = bridge.dump_efx(efx_path)
root = io_tree.build_root_from_efxfile(data, scene.collection, os.path.basename(efx_path))
root.efx_source_filename = os.path.basename(efx_path)
root.efx_source_dir = os.path.dirname(efx_path)
scene.efx_re_active_root = root
scene.efx_re_sim_scope = "GROUP"
scene.efx_re_sim_group = "ALL"
scene.efx_re_sim_seed = 0
scene.efx_re_sim_fps = 60
scene.efx_re_sim_use_textures = True

count = sim_preview.rebuild_tracks(bpy.context, keep_frame=False)
result = {
    "blender": bpy.app.version_string,
    "plugin": "mhws_efx_editor-0.3.0",
    "efx": efx_path,
    "scope": "GROUP/ALL",
    "seed": 0,
    "fps": 60,
    "entry_count": count,
    "initial_error": sim_preview._P["error"],
    "frames": [],
}
for frame in (0, 3, 6, 9, 12, 30):
    frame_data = {"frame": frame, "entries": []}
    for tr in sim_preview.tracks():
        tr["sim"].run_to(frame)
    sim_preview._rebuild_items()
    for tr in sim_preview.tracks():
        items = tr["items"]
        entry = {
            "name": tr["name"],
            "particles": len(tr["sim"].em.particles),
            "render_items": len(items),
            "kinds": dict(Counter(it.kind for it in items)),
            "texture_keys": sorted({str(it.tex_key) for it in items if it.tex_key}),
            "bounds": None,
            "first_items": [],
        }
        if items:
            xyz = [tuple(it.pos) for it in items]
            entry["bounds"] = {
                "min": [min(p[i] for p in xyz) for i in range(3)],
                "max": [max(p[i] for p in xyz) for i in range(3)],
            }
        for it in items[:4]:
            entry["first_items"].append({
                "kind": it.kind, "pos": tuple(it.pos), "size": tuple(it.size),
                "color": list(it.color), "uv_rect": it.uv_rect,
                "blend": it.blend, "tex_key": str(it.tex_key) if it.tex_key else None,
            })
        frame_data["entries"].append(entry)
    result["frames"].append(frame_data)
result["final_error"] = sim_preview._P["error"]
with open(output_path, "w", encoding="utf-8") as f:
    json.dump(result, f, ensure_ascii=False, indent=2)
print("PARITY_REFERENCE_OK", count, output_path, "error=", result["final_error"])

if image_path:
    gpu.init()
    width = height = 1024
    offscreen = gpu.types.GPUOffScreen(width, height)
    for tr in sim_preview.tracks():
        tr["sim"].run_to(3)
    sim_preview._rebuild_items()
    projection = Matrix(((0.33, 0, 0, 0), (0, 0.33, 0, 0),
                         (0, 0, -0.1, 0), (0, 0, 0, 1)))
    class View:
        view_matrix = Matrix.Identity(4)
    with offscreen.bind():
        gpu.state.active_framebuffer_get().clear(color=(0.03, 0.05, 0.09, 1.0), depth=1.0)
        gpu.matrix.load_projection_matrix(projection)
        gpu.matrix.load_matrix(Matrix.Identity(4))
        buckets, lines, line_cols = {}, [], []
        for tr in sim_preview.tracks():
            sim_preview._collect(tr, View(), buckets, lines, line_cols)
        gpu.state.blend_set("ALPHA")
        gpu.state.depth_test_set("LESS_EQUAL")
        gpu.state.depth_mask_set(False)
        flat = sim_preview._builtin("FLAT_COLOR")
        for key, bucket in buckets.items():
            if not bucket["pos"]:
                continue
            tex_key = key[1] if isinstance(key, tuple) else key
            tex = sim_preview._gpu_texture(tex_key) if tex_key else None
            if tex is None:
                batch_for_shader(flat, "TRIS", {"pos": bucket["pos"], "color": bucket["col"]}).draw(flat)
            else:
                shader = sim_preview._mesh_shader() if isinstance(key, tuple) else sim_preview._tex_shader()
                shader.bind()
                shader.uniform_sampler("image", tex)
                batch_for_shader(shader, "TRIS", {"pos": bucket["pos"], "texCoord": bucket["uv"],
                                                "color": bucket["col"]}).draw(shader)
        pixels = np.array(gpu.state.active_framebuffer_get().read_color(0, 0, width, height, 4, 0, "FLOAT"),
                          dtype=np.float32).reshape(height, width, 4)
        image = bpy.data.images.new("EFX Reference", width=width, height=height, alpha=True)
        image.pixels.foreach_set(pixels.ravel())
        image.file_format = "PNG"
        image.filepath_raw = image_path
        image.save()
        gpu.state.depth_mask_set(True)
        gpu.state.blend_set("NONE")
    offscreen.free()
    print("PARITY_REFERENCE_IMAGE", image_path, "buckets", len(buckets), "error=", sim_preview._P["error"])
