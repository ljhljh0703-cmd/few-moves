#!/usr/bin/env python3
"""Generate the Few Moves Blender source, FBX mesh kit, and room-01 art reference.

Run with Blender 5.2.2: blender -b --factory-startup --python this_file -- --stage assets
Then: blender -b few-moves-kit.blend --python this_file -- --stage render
"""

import argparse
import hashlib
import json
import math
from pathlib import Path
import sys

import bpy
from mathutils import Vector


def arguments():
    raw = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    parser = argparse.ArgumentParser()
    parser.add_argument("--stage", choices=("assets", "render"), required=True)
    return parser.parse_args(raw)


OUT = Path(__file__).resolve().parent
REPO = OUT.parents[2]
ROOM_FILE = REPO / "Assets/Nectorial/Resources/SlideRooms/room-01.json"
BLEND_FILE = OUT / "few-moves-kit.blend"
FBX_FILE = OUT / "few-moves-pieces.fbx"
MANIFEST_FILE = OUT / "mesh-manifest.json"
RENDER_FILE = OUT / "room-01-art-reference.png"
PALETTE = {
    "cream": "#eee7d8",
    "charcoal": "#293238",
    "blue": "#4374b7",
    "gold": "#d5a043",
    "teal": "#377e78",
}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def srgb_to_linear(v):
    c = int(v, 16) / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def rgb(hex_color):
    return tuple(srgb_to_linear(hex_color[i:i+2]) for i in (1, 3, 5))


def material(name, hex_color, roughness=0.78):
    m = bpy.data.materials.new(name)
    m.diffuse_color = (*rgb(hex_color), 1)
    m.use_nodes = True
    bsdf = m.node_tree.nodes.get("Principled BSDF")
    bsdf.inputs["Base Color"].default_value = (*rgb(hex_color), 1)
    bsdf.inputs["Roughness"].default_value = roughness
    bsdf.inputs["Metallic"].default_value = 0
    return m


def move_to_collection(obj, collection):
    for old in tuple(obj.users_collection):
        old.objects.unlink(obj)
    collection.objects.link(obj)


def apply_geometry(obj, bevel):
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    if bevel:
        modifier = obj.modifiers.new("Soft machined edge", "BEVEL")
        modifier.width = bevel
        modifier.segments = 2
        modifier.affect = "EDGES"
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    for face in obj.data.polygons:
        face.use_smooth = False
    obj.select_set(False)


def cube(name, width, depth, height, bevel, mat, collection):
    bpy.ops.mesh.primitive_cube_add(size=1, location=(0, 0, height / 2))
    obj = bpy.context.object
    obj.name = name
    obj.scale = (width, depth, height)
    apply_geometry(obj, bevel)
    # Move mesh coordinates so the object's origin is ground center.
    for vertex in obj.data.vertices:
        vertex.co.z += height / 2
    obj.location = (0, 0, 0)
    obj.data.materials.append(mat)
    move_to_collection(obj, collection)
    return obj


def cylinder(name, radius, height, bevel, mat, collection, segments=24):
    bpy.ops.mesh.primitive_cylinder_add(vertices=segments, radius=radius,
                                        depth=height, location=(0, 0, height / 2))
    obj = bpy.context.object
    obj.name = name
    apply_geometry(obj, bevel)
    for vertex in obj.data.vertices:
        vertex.co.z += height / 2
    obj.location = (0, 0, 0)
    obj.data.materials.append(mat)
    move_to_collection(obj, collection)
    return obj


def instance(source, name, x, y, z, collection):
    obj = bpy.data.objects.new(name, source.data)
    obj.location = (x, y, z)
    collection.objects.link(obj)
    return obj


def aim(obj, point):
    obj.rotation_euler = (Vector(point) - obj.location).to_track_quat("-Z", "Y").to_euler()


def make_room(objects, room, collection, mats):
    assert room["Width"] == 8 and room["Height"] == 8
    rows = room["Rows"]
    assert len(rows) == 8 and all(len(row) == 8 for row in rows)
    assert set("".join(rows)) <= {"#", "."}
    pieces = room["Pieces"]
    assert len(pieces) == 1 and pieces[0]["Id"] == "target"
    assert room["TargetPieceIndex"] == 0
    goal = room["Goal"]
    assert rows[goal["Y"]][goal["X"]] == "."
    start = pieces[0]["Start"]
    assert rows[start["Y"]][start["X"]] == "."
    assert (start["X"], start["Y"]) == (1, 1)
    assert (goal["X"], goal["Y"]) == (4, 3)

    for y, row in enumerate(rows):
        for x, mark in enumerate(row):
            instance(objects["FloorTile"], f"Floor_{x:02d}_{y:02d}",
                     x, -y, 0, collection)
            if mark == "#":
                instance(objects["WallTile"], f"Wall_{x:02d}_{y:02d}",
                         x, -y, 0.07, collection)
    instance(objects["GoalDisk"], "Room01_Goal", goal["X"], -goal["Y"], 0.077, collection)
    instance(objects["TargetPuck"], "Room01_Target", start["X"], -start["Y"], 0.078, collection)

    # This plinth is render staging only. It is not exported to the reusable FBX kit.
    tray = cube("Reference_Backdrop", 8.45, 8.45, 0.11, 0.06,
                mats["charcoal"], collection)
    tray.location = (3.5, -3.5, -0.13)
    ground = cube("Studio_Backdrop", 200, 200, 0.04, 0, mats["backdrop"], collection)
    ground.location = (3.5, -3.5, -0.26)


def add_lighting_and_camera(collection):
    camera_data = bpy.data.cameras.new("Room01_Ortho_Data")
    camera = bpy.data.objects.new("Room01_Ortho", camera_data)
    collection.objects.link(camera)
    camera.location = (7.3, 4.2, 25.0)
    aim(camera, (3.5, -3.5, 0))
    camera_data.type = "ORTHO"
    camera_data.ortho_scale = 11.9
    bpy.context.scene.camera = camera

    key_data = bpy.data.lights.new("Large Softbox", "AREA")
    key = bpy.data.objects.new("Large Softbox", key_data)
    collection.objects.link(key)
    key.location = (-3.5, 2.5, 12)
    key_data.energy = 850
    key_data.shape = "DISK"
    key_data.size = 9
    aim(key, (3.5, -3.5, 0))

    fill_data = bpy.data.lights.new("Soft Fill", "AREA")
    fill = bpy.data.objects.new("Soft Fill", fill_data)
    collection.objects.link(fill)
    fill.location = (11, -8, 9)
    fill_data.energy = 380
    fill_data.shape = "DISK"
    fill_data.size = 8
    aim(fill, (3.5, -3.5, 0))


def mesh_stats(obj):
    vertices = [obj.matrix_world @ v.co for v in obj.data.vertices]
    bounds = {
        "min": [round(min(v[i] for v in vertices), 5) for i in range(3)],
        "max": [round(max(v[i] for v in vertices), 5) for i in range(3)],
    }
    return {
        "name": obj.name,
        "vertices": len(obj.data.vertices),
        "triangles": sum(len(poly.vertices) - 2 for poly in obj.data.polygons),
        "bounds_blender_xyz": bounds,
        "origin_blender_xyz": [0, 0, 0],
        "material": obj.data.materials[0].name,
    }


def build_assets():
    bpy.ops.object.select_all(action="SELECT")
    bpy.ops.object.delete(use_global=False)
    for collection in tuple(bpy.data.collections):
        if collection.users == 0:
            bpy.data.collections.remove(collection)

    scene = bpy.context.scene
    library = bpy.data.collections.new("EXPORT_LIBRARY__six_reusable_meshes")
    scene.collection.children.link(library)
    library.hide_render = True
    reference = bpy.data.collections.new("ROOM_01_REFERENCE__not_exported")
    scene.collection.children.link(reference)

    mats = {name: material(name.capitalize() + "_Mat", color)
            for name, color in PALETTE.items()}
    mats["backdrop"] = material("Warm_Backdrop_Mat", "#ded8cb")

    objects = {
        "FloorTile": cube("FloorTile", 0.985, 0.985, 0.07, 0.014,
                          mats["cream"], library),
        "WallTile": cube("WallTile", 0.94, 0.94, 0.32, 0.038,
                         mats["charcoal"], library),
        "TargetPuck": cylinder("TargetPuck", 0.35, 0.20, 0.027,
                               mats["blue"], library),
        "HelperSquare": cube("HelperSquare", 0.64, 0.64, 0.19, 0.035,
                             mats["teal"], library),
        "HelperDiamond": cube("HelperDiamond", 0.47, 0.47, 0.19, 0.03,
                              mats["gold"], library),
        "GoalDisk": cylinder("GoalDisk", 0.30, 0.035, 0.009,
                             mats["gold"], library),
    }
    diamond = objects["HelperDiamond"]
    for vertex in diamond.data.vertices:
        x, y = vertex.co.x, vertex.co.y
        vertex.co.x = (x-y) / math.sqrt(2)
        vertex.co.y = (x+y) / math.sqrt(2)
    diamond.data.update()

    room = json.loads(ROOM_FILE.read_text(encoding="utf-8"))
    make_room(objects, room, reference, mats)
    add_lighting_and_camera(reference)

    scene.render.engine = "CYCLES"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = True
    scene.render.resolution_x = 1200
    scene.render.resolution_y = 1200
    scene.render.resolution_percentage = 100
    scene.render.image_settings.file_format = "PNG"
    scene.render.filepath = str(RENDER_FILE)
    scene.world.color = (0.52, 0.49, 0.44)
    scene.render.film_transparent = False
    scene.view_settings.view_transform = "AgX"
    scene.view_settings.look = "AgX - Medium High Contrast"

    bpy.ops.wm.save_as_mainfile(filepath=str(BLEND_FILE), compress=True)
    bpy.ops.object.select_all(action="DESELECT")
    for obj in objects.values():
        obj.select_set(True)
    bpy.context.view_layer.objects.active = objects["FloorTile"]
    bpy.ops.export_scene.fbx(
        filepath=str(FBX_FILE), use_selection=True, object_types={"MESH"},
        use_mesh_modifiers=True, add_leaf_bones=False,
        axis_forward="-Z", axis_up="Y", apply_unit_scale=True,
        bake_space_transform=True, path_mode="AUTO", embed_textures=False,
    )
    stats = [mesh_stats(objects[name]) for name in objects]
    manifest = {
        "state": "provisional",
        "gate": "runtime_import_pending",
        "source_room": str(ROOM_FILE.relative_to(REPO)),
        "source_room_sha256": sha256(ROOM_FILE),
        "room_id": room["Id"],
        "room_size": [room["Width"], room["Height"]],
        "room_rows": room["Rows"],
        "room_target_xy": [room["Pieces"][0]["Start"]["X"], room["Pieces"][0]["Start"]["Y"]],
        "room_goal_xy": [room["Goal"]["X"], room["Goal"]["Y"]],
        "blender_version": bpy.app.version_string,
        "unit": "1 cell = 1 Blender unit = 1 Unity unit",
        "mesh_origin": "Each reusable mesh origin is center of bottom face; local z=0 is ground.",
        "blender_to_unity": "FBX axis_forward=-Z, axis_up=Y, bake_space_transform=True; Blender (x,y,z) maps to Unity (x,z,y). Room data (X,Y) maps to Blender (X,-Y) and Unity (X, height, -Y).",
        "palette_srgb": PALETTE,
        "textures": "none; embedded material colors only",
        "fbx_sha256": sha256(FBX_FILE),
        "blend_sha256": sha256(BLEND_FILE),
        "total_source_triangles": sum(s["triangles"] for s in stats),
        "meshes": stats,
        "reference_scene": "Room 01 instances only. Helper meshes are library objects and absent from the room render.",
    }
    MANIFEST_FILE.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print("FROZEN_FBX", FBX_FILE, manifest["fbx_sha256"])
    print("FROZEN_BLEND", BLEND_FILE, manifest["blend_sha256"])
    print("SOURCE_TRIANGLES", manifest["total_source_triangles"])


def render_room():
    scene = bpy.context.scene
    scene.render.filepath = str(RENDER_FILE)
    bpy.ops.render.render(write_still=True)
    print("RENDER_SHA256", RENDER_FILE, sha256(RENDER_FILE))


if __name__ == "__main__":
    args = arguments()
    if args.stage == "assets":
        build_assets()
    else:
        render_room()
