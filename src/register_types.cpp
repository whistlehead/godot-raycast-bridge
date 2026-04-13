#include "register_types.h"
#include "raycast_bridge.h"

#include <godot_cpp/core/defs.hpp>
#include <godot_cpp/godot.hpp>

using namespace godot;

void initialize_raycast_bridge(ModuleInitializationLevel p_level)
{
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) return;
    ClassDB::register_class<RaycastBridge>();
}

void uninitialize_raycast_bridge(ModuleInitializationLevel p_level)
{
    (void)p_level; // nothing to tear down
}

extern "C" {

GDExtensionBool GDE_EXPORT raycast_bridge_init(
    GDExtensionInterfaceGetProcAddress p_get_proc_address,
    const GDExtensionClassLibraryPtr   p_library,
    GDExtensionInitialization*         r_initialization)
{
    GDExtensionBinding::InitObject init_obj(
        p_get_proc_address, p_library, r_initialization);

    init_obj.register_initializer(initialize_raycast_bridge);
    init_obj.register_terminator(uninitialize_raycast_bridge);
    init_obj.set_minimum_library_initialization_level(MODULE_INITIALIZATION_LEVEL_SCENE);

    return init_obj.init();
}

} // extern "C"
